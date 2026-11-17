# The whole program: skip public holidays on a daily schedule

The how-to is [/learn/114-holiday-calendar.md](/learn/114-holiday-calendar.md). This file holds every source file of `learn/100-scheduling/114-holiday-calendar` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### holiday-calendar.cs

```csharp
// How to skip public holidays on a daily schedule
// A cron cannot name the days a country takes off. An occurrence calendar can: local times on the weekdays you list,
// minus the dates you exclude. The list is data an operator publishes, so next year's holidays need no deploy.
using System.Globalization;
using Acta;
using Acta.Learn.HolidayCalendar;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A family in sys is shared: every namespace binds it by its plain name.
var workingDays = new CalendarLookup("sys", "working-days");
var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");
string Local(DateTime utc) =>
    TimeZoneInfo
        .ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
        .ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

// Phase 1: a deploy step publishes the calendar before the jobs that name it start. An enqueue-only host (no Run<>)
// still bootstraps the schema, and the operations need no worker.
long current;
{
    var seed = Host.CreateApplicationBuilder(args);
    seed.Services.UseActa(j => j.UseLocalDatabase(seed.Configuration));
    using var seedHost = seed.Build();
    await seedHost.StartAsync();

    var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;

    // learn: 2. Preview the draft, then publish it
    // The preview answers the draft's next occurrences from any instant, in the zone you name. This one starts on the
    // Friday before a Monday holiday. When the list changes, save the new definition and publish it again: every
    // schedule that names the calendar follows the new version.
    var draft = await calendars.SaveDraftAsync(workingDays, CalendarRoleCode.Occurrence, Definitions.WorkingDays);
    var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
    var preview = (await calendars.PreviewAsync(workingDays, draft.Version!.Value, friday, count: 3, timeZoneId: "Europe/Ljubljana"))!;
    foreach (var occurrence in preview.OccurrencesUtc)
    {
        Console.WriteLine($"reports {Local(occurrence)}");
    }

    await calendars.PublishAsync(workingDays, draft.Version.Value, reasonMessage: "Holidays through February 2028");
    // learn: end
    current = draft.Version.Value;

    await seedHost.StopAsync();
}

// Phase 2: the start deploys daily-report, whose schedule names working-days. The deploy checks that the family has
// a published version.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<HolidayCalendarJobs>("reports");
});

using var host = builder.Build();
await host.StartAsync();

// The schedule has no expression of its own: its next occurrence is the calendar's next instant. The namespace also
// lists the system jobs' schedules (alerts, retention), so the read names the job.
var operations = host.Services.GetRequiredService<IActaOperations>();
var schedule = (
    await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "reports", JobName: "daily-report"))
).Items.Single();
var next = (await operations.Calendars.PreviewAsync(workingDays, current, count: 1, timeZoneId: "Europe/Ljubljana"))!.OccurrencesUtc[0];
Console.WriteLine(
    $"Next report {Local(schedule.NextOccurrenceAtUtc!.Value)}; the calendar's next instant: {schedule.NextOccurrenceAtUtc == next}"
);

await host.StopAsync();

namespace Acta.Learn.HolidayCalendar
{
    public readonly record struct DailyReport;

    public static class Definitions
    {
        // learn: 1. List the holidays in a calendar definition
        // A `daily-local-time` definition fires at each of `times` on each of `days`, and skips every date in
        // `excludedDates`. Paste the whole list of holidays: one that falls on a weekend is skipped twice over.
        public const string WorkingDays = """
            {
              "kind": "daily-local-time",
              "times": ["08:00"],
              "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
              "excludedDates": ["2027-11-01", "2027-12-25", "2027-12-26", "2028-01-01", "2028-01-02", "2028-02-08"]
            }
            """;
        // learn: end
    }

    public sealed class DailyReportJob
    {
        [Job("daily-report")]
        // learn: 3. Name the calendar on the schedule
        // `Calendar` takes the place of a cron or an interval, and `TimeZoneId` is the zone the calendar's local times
        // are read in.
        [Schedule("working-days-at-eight", Calendar = "working-days", TimeZoneId = "Europe/Ljubljana")]
        // learn: end
        public Task Handle(DailyReport input, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd} sent the daily report");
            return Task.CompletedTask;
        }
    }
}
```

