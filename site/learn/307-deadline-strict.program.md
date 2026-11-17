# The whole program: drop a job that is past its deadline

The how-to is [/learn/307-deadline-strict.md](/learn/307-deadline-strict.md). This file holds every source file of `concepts/300-failure-and-recovery/307-deadline-strict` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### deadline-strict.cs

```csharp
// How to drop a job that is past its deadline
// A `Deadline` is the whole job's budget, measured from its creation. Under the default, strict behaviour an overdue
// job is cancelled before its handler runs.
using Acta;
using Acta.Concepts.DeadlineStrict;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DeadlineStrictJobs>("deadline-strict");
});

// Quiet the framework's failure logging; the job is cancelled on purpose.
builder.Logging.AddFilter("Acta", LogLevel.Error);

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

// The deadline is 2s after creation, but Delayed holds the first run for 4s, so by the time the
// worker can claim it the job is already overdue. A Strict deadline cancels it at admission and the
// handler never runs. Delayed() and the deadline both resolve on the database clock.
var handle = await jobs.ScheduleAsync(new GenerateReport("q3"), TimeSpan.FromSeconds(4));
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] enqueued; 2s deadline, but the first run is held 4s.");

GetJobResponse? snapshot;
do
{
    await Task.Delay(500);
    snapshot = await jobs.GetAsync(handle);
} while (snapshot is null or { Status: not (JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled) });

// Cancelled (the finishing event reason is JobDeadlineExceeded). FailureCount stays 0: a deadline
// cancel does not consume the retry budget.
Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] status={snapshot.Status} failureCount={snapshot.FailureCount}");

await host.StopAsync();

namespace Acta.Concepts.DeadlineStrict
{
    public sealed record GenerateReport(string Quarter);

    public static class GenerateReportJob
    {
        // learn: Give the job a deadline
        // Deadline is the whole-job budget measured from creation, stable across retries (unlike 304's
        // ExecutionTimeout, which caps a single attempt). Strict is the default: an overdue job is
        // cancelled at admission, and a retry that would land past the deadline is refused re-arm.
        [Job("generate-report", Deadline = "2s")]
        // learn: end
        public static void Handle(GenerateReport input) =>
            Console.WriteLine($"building the {input.Quarter} report (never printed; the job was overdue before it ran)");
    }
}
```

