# The whole program: run a job every day at a set hour in your time zone

The how-to is [/learn/104-timezone-schedule.md](/learn/104-timezone-schedule.md). This file holds every source file of `learn/100-scheduling/104-timezone-schedule` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### timezone-schedule.cs

```csharp
// How to run a job every day at a set hour in your time zone
// A cron with `TimeZoneId` fires at the same local hour all year: `0 8 * * *` in `Europe/Berlin` is 08:00 in Berlin,
// in summer and in winter.
using Acta;
using Acta.Learn.TimezoneSchedule;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<TimezoneScheduleJobs>("timezone-schedule");
});

using var host = builder.Build();
await host.StartAsync();

Console.WriteLine("Two schedules on one job: the interval one fires every 15s so you see it work;");
Console.WriteLine("the zoned one fires 08:00 Ljubljana wall-clock, summer and winter alike. Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();

namespace Acta.Learn.TimezoneSchedule
{
    public readonly record struct DailyDigest;

    public sealed class DailyDigestJob
    {
        [Job("daily-digest", EventRecording = EventRecordingCode.Failures)]
        // learn: Write the hour as a cron and pin it to your time zone
        // The cron has five fields: minute, hour, day of month, month, day of week. `0 8 * * *` is 08:00 every day, and
        // without a time zone that hour is read as UTC.
        // TimeZoneId pins cron to wall-clock local time across DST switches (IANA or Windows ids; an
        // unresolvable id fails the deploy, which a development start runs). For Berlin write `Europe/Berlin`.
        [Schedule("morning-ljubljana", "0 8 * * *", TimeZoneId = "Europe/Ljubljana")]
        // learn: end
        // The second schedule is here so the program prints while you watch. Intervals are absolute gaps and take no timezone.
        [Schedule("demo-tick", "PT15S")]
        public Task Handle(DailyDigest input, JobContext context, CancellationToken ct)
        {
            Console.WriteLine(
                $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
            );
            return Task.CompletedTask;
        }
    }
}
```

