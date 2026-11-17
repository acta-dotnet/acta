# The whole program: run a job on a repeating schedule

The how-to is [/learn/101-recurring-job.md](/learn/101-recurring-job.md). This file holds every source file of `learn/100-scheduling/101-recurring-job` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### recurring-job.cs

```csharp
// How to run a job on a repeating schedule
// `[Schedule]` with a cron makes one permanent job that fires on that cadence, with nothing to enqueue.
using Acta;
using Acta.Learn.Recurring;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<RecurringJobs>("recurring-job");
});

// No enqueue: the deploy creates one permanent job for the [Schedule] below, and it fires on its cron cadence.
await builder.Build().RunAsync();

namespace Acta.Learn.Recurring
{
    public readonly record struct PurgeStaleSessions;

    public sealed class PurgeStaleSessionsJob
    {
        // learn: Put a schedule on the job
        // `[Schedule]` takes a name and a cron, here one of the `Cron` constants, read in UTC unless `TimeZoneId`
        // says otherwise. One permanent job fires on the cadence, and the count each firing returns replaces the
        // result the last one stored. `EventRecording = Failures` is optional: it keeps a frequent schedule from
        // writing an event for every firing that succeeds. Nothing enqueues a scheduled job, so its input is an
        // empty record.
        [Job("purge-stale-sessions", EventRecording = EventRecordingCode.Failures)]
        [Schedule("every-15-seconds", Cron.Every15Seconds)]
        // learn: end
        public async Task<int> Handle(PurgeStaleSessions input, JobContext context, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            var purged = Random.Shared.Next(0, 25);
            Console.WriteLine(
                $"{DateTime.UtcNow:HH:mm:ss} UTC [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}] purged {purged} stale sessions"
            );
            return purged;
        }
    }
}
```

