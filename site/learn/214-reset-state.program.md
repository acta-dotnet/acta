# The whole program: start each firing of a recurring job clean

The how-to is [/learn/214-reset-state.md](/learn/214-reset-state.md). This file holds every source file of `learn/200-durable-execution/214-reset-state` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### reset-state.cs

```csharp
// How to start each firing of a recurring job clean
// A permanent job keeps its variables from one firing to the next. `ResetStateAsync` at the end of a firing clears
// them, so the next one starts blank.
using Acta;
using Acta.Learn.ResetState;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ResetStateJobs>("reset-state");
});

// The [Schedule] recurs every 5s, so there's nothing to enqueue; just run the worker.
await builder.Build().RunAsync();

namespace Acta.Learn.ResetState
{
    public readonly record struct CollectMetrics;

    public sealed class MetricsMonitor
    {
        // Each fire re-runs the same durable job, so variables persist across fires unless the handler
        // clears its durable state as the final act of the cycle.
        [Job("collect-metrics", EventRecording = EventRecordingCode.Failures)]
        [Schedule("every-5-seconds", "PT5S")]
        public async Task Handle(CollectMetrics input, JobContext context, CancellationToken ct)
        {
            // Reads 0 because the previous cycle's reset wiped this variable; without it, reads 3.
            var carried = await context.GetVariableOrDefaultAsync("reading-count", 0, ct);
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC cycle start - {carried} readings carried over (0 means last cycle reset)");

            // Write each reading's count to durable state, so the reset below has something to clear.
            var sum = 0;
            for (var i = 1; i <= 3; i++)
            {
                var reading = Random.Shared.Next(18, 25);
                sum += reading;
                await context.SetVariableAsync("reading-count", i, ct);
                await Task.Delay(100, ct);
            }
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC cycle done  - 3 readings, avg {sum / 3}C");

            // learn: Clear the job's durable state as the last act of the firing
            // Clear this cycle's durable state so the next fire starts blank.
            await context.ResetStateAsync(ct);
            // learn: end
        }
    }
}
```

