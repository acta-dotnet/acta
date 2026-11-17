# The whole program: check, delete, and default a job's variables

The how-to is [/learn/216-variable-lifecycle.md](/learn/216-variable-lifecycle.md). This file holds every source file of `learn/200-durable-execution/216-variable-lifecycle` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### variable-lifecycle.cs

```csharp
// How to check, delete, and default a job's variables
// Beyond `GetOrSetVariableAsync`, shown across a retry in 203, a handler can check for a variable, delete it, read it
// with a default, and store a raw `JobPayload`.
using Acta;
using Acta.Learn.VariableLifecycle;
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

await jobs.CallAsync(new InspectVariables());

await host.StopAsync();

namespace Acta.Learn.VariableLifecycle
{
    public readonly record struct InspectVariables;

    public sealed class InspectVariablesJob
    {
        [Job("inspect-variables")]
        public async Task Handle(InspectVariables input, JobContext context, CancellationToken ct)
        {
            var factoryCalls = 0;
            // learn: 1. Compute a value once
            // GetOrSet: factory runs on the first call; the second call returns the stored value without invoking the factory.
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
            // learn: end
            Console.WriteLine($"get-or-set: v1={v1} v2={v2} factory-calls={factoryCalls}");

            // ExistsVariableAsync: the variable is present right after get-or-set.
            var existsBefore = await context.ExistsVariableAsync("counter", ct);
            Console.WriteLine($"exists before delete: {existsBefore}");

            // learn: 2. Delete a variable, and check whether one is there
            // DeleteVariableAsync removes the variable; Exists is false immediately after.
            await context.DeleteVariableAsync("counter", ct);
            var existsAfter = await context.ExistsVariableAsync("counter", ct);
            // learn: end
            Console.WriteLine($"exists after delete: {existsAfter}");

            // learn: 3. Read a variable that may not be there
            // GetVariableOrDefaultAsync with a default value: absent variable returns the supplied default.
            var fallback = await context.GetVariableOrDefaultAsync("counter", "missing", ct);
            // learn: end
            Console.WriteLine($"default fallback: {fallback}");

            // Raw JobPayload variable: store a pre-serialized payload; the format travels with the value.
            var raw = JobPayload.Json("raw-value");
            await context.SetVariableAsync("raw-var", raw, ct);
            var rawBack = await context.GetVariableOrDefaultAsync<string>("raw-var", ct);
            Console.WriteLine($"raw payload round-trip: {rawBack}");
        }
    }
}
```

