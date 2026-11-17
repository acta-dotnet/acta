# The whole program: pause and resume a schedule

The how-to is [/learn/105-schedule-control.md](/learn/105-schedule-control.md). This file holds every source file of `concepts/100-scheduling/105-schedule-control` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### schedule-control.cs

```csharp
// How to pause and resume a schedule
// `operations.Schedules` pauses a schedule from outside the handler, for good or until an instant, and resumes it.
using Acta;
using Acta.Concepts.ScheduleControl;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScheduleControlJobs>("schedule-control");
});

using var host = builder.Build();
await host.StartAsync();

var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;
var queries = host.Services.GetRequiredService<IActaOperations>();

// learn: 1. Name the schedule
// A schedule is named by its namespace, its job, and its own name.
// The development start deploys the schedule before the worker starts, so its row exists once StartAsync returns.
var lookup = new ScheduleLookup("schedule-control", "hourly-report", "every-hour");

// learn: end

Console.WriteLine("Pausing schedule indefinitely...");

// learn: 2. Pause it until someone resumes it
// An indefinite pause: the schedule does not fire until an operator explicitly resumes it.
var r1 = await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "maintenance window");

// learn: end
Console.WriteLine($"After indefinite pause: status={r1.Status} next-run={r1.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

// learn: 3. Or pause it until an instant
// A timed pause: the scheduler resumes the schedule by itself when the timestamp passes.
var resumeAt = DateTime.UtcNow.AddHours(2);
Console.WriteLine($"Replacing with timed pause until {resumeAt:O}...");
var r2 = await schedules.PauseAsync(lookup, untilUtc: resumeAt, reasonMessage: "drain window");

// learn: end
Console.WriteLine($"After timed pause: status={r2.Status} paused-until={r2.PausedUntilUtc?.ToString("O") ?? "(none)"}");

Console.WriteLine("Resuming schedule...");

// learn: 4. Resume it
// A resume clears the pause and keeps the cursor; the next admission pass decides any missed occurrence.
var r3 = await schedules.ResumeAsync(lookup, reasonMessage: "drain complete");

// learn: end
Console.WriteLine($"After resume: status={r3.Status} next-run={r3.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

// Confirm via the query surface that the schedule is active again and has a next-run time.
var page = await queries.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "schedule-control"));
var item = page.Items.FirstOrDefault(i => i.ScheduleName == "every-hour");
Console.WriteLine($"Query confirms: status={item?.Status} next-run={item?.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

await host.StopAsync();

namespace Acta.Concepts.ScheduleControl
{
    public readonly record struct GenerateHourlyReport;

    public sealed class HourlyReportJob
    {
        // 1h interval: will not fire during the short demo run, so only schedule-control is exercised.
        [Job("hourly-report", EventRecording = EventRecordingCode.Failures)]
        [Schedule("every-hour", "1h")]
        public async Task Handle(GenerateHourlyReport input, CancellationToken ct)
        {
            await Task.Delay(100, ct);
            Console.WriteLine("hourly report generated");
        }
    }
}
```

