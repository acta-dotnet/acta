// Concept: LockScope.Global serializes jobs in different namespaces on the same key.
using Acta;
using Acta.Concepts.GlobalLock;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);

    // Two independent namespaces; each runs its own worker and claim loop.
    j.Run<GlobalLockJobs>("ns-alpha");
    j.Run<GlobalLockJobs>("ns-beta");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Enqueue one job per namespace; both will race for the same global lock.
var alpha = await jobs.EnqueueAsync(
    new JobEnqueueRequest("ns-alpha", "critical-section", JobPayload.Json(new CriticalSection("alpha"))),
    CancellationToken.None
);
var beta = await jobs.EnqueueAsync(
    new JobEnqueueRequest("ns-beta", "critical-section", JobPayload.Json(new CriticalSection("beta"))),
    CancellationToken.None
);

Console.WriteLine("Enqueued jobs in ns-alpha and ns-beta; both contend for the same global lock.");

// Poll until both are terminal.
while (true)
{
    var sa = await jobs.GetStatusAsync(alpha);
    var sb = await jobs.GetStatusAsync(beta);
    if (sa is { } a && sb is { } b && a.IsTerminal && b.IsTerminal)
    {
        Console.WriteLine($"ns-alpha: {a}  ns-beta: {b}");
        Console.WriteLine("Both jobs finished; lock scope confirmed global.");
        break;
    }
    await Task.Delay(100);
}

await host.StopAsync();

namespace Acta.Concepts.GlobalLock
{
    public sealed record CriticalSection(string Origin);

    public sealed class CriticalSectionJob
    {
        [Job("critical-section")]
        public async Task Handle(CriticalSection input, JobContext ctx, CancellationToken ct)
        {
            // LockScope.Global: the key "shared-resource" is cluster-wide, so jobs from ns-alpha
            // and ns-beta serialize on it. With LockScope.Namespace (the default) they would run
            // concurrently because each namespace scopes its own copy.
            await ctx.RunWithLockAsync(
                "shared-resource",
                async () =>
                {
                    Console.WriteLine($"[{input.Origin}] acquired global lock, running critical section...");
                    await Task.Delay(500, ct);
                    Console.WriteLine($"[{input.Origin}] done, releasing global lock");
                },
                scope: LockScope.Global,
                ct: ct
            );
        }
    }
}
