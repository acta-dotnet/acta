// Concept: durable per-job variable lifecycle - GetOrSet computes once, Exists, default fallback, Delete, raw JobPayload.
using Acta;
using Acta.Concepts.VariableLifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<VariableLifecycleJobs>("variable-lifecycle");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

await jobs.RunAndWaitAsync(new InspectVariables());

await host.StopAsync();

namespace Acta.Concepts.VariableLifecycle
{
    public readonly record struct InspectVariables;

    public sealed class InspectVariablesJob
    {
        [Job("inspect-variables")]
        public async Task Handle(InspectVariables input, JobContext context, CancellationToken ct)
        {
            // GetOrSet: factory runs on the first call; the second call returns the stored value without invoking the factory.
            var factoryCalls = 0;
            var v1 = await context.GetOrSetVariableAsync(
                "counter",
                () =>
                {
                    factoryCalls++;
                    return 42;
                },
                ct
            );
            var v2 = await context.GetOrSetVariableAsync(
                "counter",
                () =>
                {
                    factoryCalls++;
                    return 99;
                },
                ct
            );
            Console.WriteLine($"get-or-set: v1={v1} v2={v2} factory-calls={factoryCalls}");

            // ExistsVariableAsync: the variable is present right after get-or-set.
            var existsBefore = await context.ExistsVariableAsync("counter", ct);
            Console.WriteLine($"exists before delete: {existsBefore}");

            // DeleteVariableAsync removes the variable; Exists is false immediately after.
            await context.DeleteVariableAsync("counter", ct);
            var existsAfter = await context.ExistsVariableAsync("counter", ct);
            Console.WriteLine($"exists after delete: {existsAfter}");

            // GetVariableOrDefaultAsync with a default value: absent variable returns the supplied default.
            var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
            Console.WriteLine($"default fallback: {fallback}");

            // Raw JobPayload variable: store pre-serialized text bytes; the format travels with the value.
            var raw = JobPayload.Text("raw-value");
            await context.SetVariableAsync("raw-var", raw, ct);
            var rawBack = await context.GetVariableOrDefaultAsync<string>("raw-var", ct);
            Console.WriteLine($"raw payload round-trip: {rawBack}");
        }
    }
}
