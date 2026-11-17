# The whole program: run an overdue job in a reduced mode

The how-to is [/learn/308-deadline-advisory.md](/learn/308-deadline-advisory.md). This file holds every source file of `learn/300-failure-and-recovery/308-deadline-advisory` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### deadline-advisory.cs

```csharp
// How to run an overdue job in a reduced mode
// With `DeadlineBehavior = Advisory` an overdue job still runs. The handler reads `IsOverdue` and chooses a reduced
// path.
using Acta;
using Acta.Learn.DeadlineAdvisory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeadlineAdvisoryJobs>("deadline-advisory");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// Same timing as 307 (2s deadline, first run held 4s), so the job is overdue when the handler starts.
// An Advisory deadline never auto-terminates: the handler runs anyway and decides what to do.
var handle = await jobs.ScheduleAsync(new GenerateReport("q3"), TimeSpan.FromSeconds(4));
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] enqueued; 2s deadline, but the first run is held 4s.");

GetJobResponse? snapshot;
do
{
    await Task.Delay(500);
    snapshot = await jobs.GetAsync(handle);
} while (snapshot is null or { Status: not (JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled) });

// Done: the handler ran past the deadline and chose the reduced path itself.
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] status={snapshot.Status}");

await host.StopAsync();

namespace Acta.Learn.DeadlineAdvisory
{
    public sealed record GenerateReport(string Quarter);

    public static class GenerateReportJob
    {
        // learn: 1. Make the deadline advisory
        // Advisory makes the deadline informational: unlike Strict (307), the engine never cancels an
        // overdue job. The handler reads context.IsOverdue (and context.TimeUntilDeadline) and degrades on its
        // own; here it emits a quick summary instead of the expensive full report when it is late.
        [Job("generate-report", Deadline = "2s", DeadlineBehavior = DeadlineBehaviorCode.Advisory)]
        // learn: end
        public static async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
        {
            // learn: 2. Ask whether the job is overdue, and do less when it is
            if (context.IsOverdue)
            {
                Console.WriteLine(
                    $"[{context.JobRef}] running late by {context.TimeUntilDeadline}; emitting a quick {input.Quarter} summary."
                );
                return;
            }
            // learn: end

            Console.WriteLine($"[{context.JobRef}] on time; building the full {input.Quarter} report...");
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Console.WriteLine($"[{context.JobRef}] {input.Quarter} report done.");
        }
    }
}
```

