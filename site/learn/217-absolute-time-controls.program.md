# The whole program: schedule, sleep, and reschedule to an exact instant

The how-to is [/learn/217-absolute-time-controls.md](/learn/217-absolute-time-controls.md). This file holds every source file of `learn/200-durable-execution/217-absolute-time-controls` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### absolute-time-controls.cs

```csharp
// How to schedule, sleep, and reschedule to an exact instant
// Pin work to an instant instead of a delay: `ScheduleAsync(input, runAt)` starts a job then, `SleepUntilAsync`
// pauses a handler until then, and `RescheduleUntilAsync` runs the handler again then.
using Acta;
using Acta.Learn.AbsoluteTimeControls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<AbsoluteTimeControlsJobs>("absolute-time-controls");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var now = DateTimeOffset.UtcNow;

// learn: 1. Start a job at an instant
// Pin the earliest claim to an absolute wall-clock instant rather than a relative delay.
// This demo uses a two-second offset so the run stays fast.
var runAt = now.AddSeconds(2);
Console.WriteLine($"Part 1 - ScheduleAsync: enqueuing GenerateReport to run at {runAt:HH:mm:ss.fff} UTC");
var report = await jobs.ScheduleAsync(new GenerateReport("q1", SleepUntil: now.AddSeconds(4)), runAt);

// learn: end
Console.WriteLine($"  job {report.JobRef} is Ready, due later; worker will not claim it before {runAt:HH:mm:ss.fff}");

// --- Part 2: RescheduleUntilAsync ---
// The polling job starts immediately, parks itself until a specific instant on the first run,
// then observes readiness and completes on the second run.
var rescheduleAt = now.AddSeconds(2);
Console.WriteLine($"Part 2 - RescheduleUntilAsync: enqueuing PollExternal; will reschedule to {rescheduleAt:HH:mm:ss.fff} UTC");
var poll = await jobs.EnqueueAsync(new PollExternal("batch-42", RescheduleUntil: rescheduleAt));

// Wait for both to reach terminal Succeeded (poll completes ~2s out, report ~4s out after sleep)
Console.WriteLine("Waiting for jobs to complete...");
await report.WaitAsync();
await poll.WaitAsync();

var reportSnap = await jobs.GetAsync(report);
var pollSnap = await jobs.GetAsync(poll);
Console.WriteLine($"GenerateReport final status: {reportSnap!.Status}, finished at {reportSnap.ModifiedAtUtc:HH:mm:ss.fff} UTC");
Console.WriteLine($"PollExternal final status:   {pollSnap!.Status}, finished at {pollSnap.ModifiedAtUtc:HH:mm:ss.fff} UTC");

await host.StopAsync();

namespace Acta.Learn.AbsoluteTimeControls
{
    public sealed record GenerateReport(string Period, DateTimeOffset SleepUntil);

    public sealed record PollExternal(string BatchId, DateTimeOffset RescheduleUntil);

    public sealed class ReportJob
    {
        [Job("generate-report")]
        public async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine($"[generate-report] claimed at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

            // learn: 2. Pause a handler until an instant
            // SleepUntilAsync arms a named durable timer at an absolute instant; on replay after the
            // instant passes the named timer is already consumed and the handler continues.
            Console.WriteLine($"[generate-report] sleeping until absolute instant {input.SleepUntil:HH:mm:ss.fff} UTC");
            await context.SleepUntilAsync("report-delay", input.SleepUntil, ct: ct);
            // learn: end

            Console.WriteLine($"[generate-report] resumed at {DateTime.UtcNow:HH:mm:ss.fff} UTC; report for {input.Period} sent");
        }
    }

    public sealed class PollExternalJob
    {
        private static int _runs;

        [Job("poll-external")]
        public async Task Handle(PollExternal input, JobContext context, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref _runs);
            Console.WriteLine($"[poll-external] run #{run} at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

            if (run == 1)
            {
                // learn: 3. Run the handler again at an instant, without spending a retry
                // RescheduleUntilAsync re-arms the job to an absolute instant without burning the
                // retry budget; the handler restarts from the top on the next claim. The call ends this execution
                // there, so the line after the `if` runs only on the run it schedules.
                Console.WriteLine($"[poll-external] not ready; rescheduling to absolute instant {input.RescheduleUntil:HH:mm:ss.fff} UTC");
                await context.RescheduleUntilAsync(input.RescheduleUntil, "external batch not ready", ct);
                // learn: end
            }

            Console.WriteLine($"[poll-external] batch {input.BatchId} is ready; processing complete");
        }
    }
}
```

