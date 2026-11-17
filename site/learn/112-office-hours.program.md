# The whole program: keep a schedule inside office hours

The how-to is [/learn/112-office-hours.md](/learn/112-office-hours.md). This file holds every source file of `learn/100-scheduling/112-office-hours` as it is compiled.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### office-hours.cs

```csharp
// How to keep a schedule inside office hours
// An availability calendar is the open time a schedule may fire in: windows by weekday, with a date closed or given
// other hours. A schedule names it, and an occurrence outside every window is left out, never moved.
using System.Globalization;
using Acta;
using Acta.Learn.OfficeHours;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A family in sys is shared: every namespace binds it by its plain name.
var officeHours = new CalendarLookup("sys", "office-hours");
var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");
string Local(DateTime utc, string format) =>
    TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString(format, CultureInfo.InvariantCulture);

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
    // The preview answers the draft's next windows from any instant, in the zone you name, so the hours can be read
    // before a schedule follows them. This one starts on the Friday before a public holiday.
    var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OfficeHours);
    var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
    var preview = (await calendars.PreviewAsync(officeHours, draft.Version!.Value, friday, count: 5, timeZoneId: "Europe/Ljubljana"))!;
    foreach (var window in preview.Windows)
    {
        Console.WriteLine($"open {Local(window.StartUtc, "ddd yyyy-MM-dd HH:mm")} to {Local(window.EndUtc!.Value, "HH:mm")}");
    }

    await calendars.PublishAsync(officeHours, draft.Version.Value, reasonMessage: "Office hours");
    // learn: end
    current = draft.Version.Value;

    await seedHost.StopAsync();
}

// Phase 2: the start deploys sync-crm, whose schedule names office-hours. The deploy checks that the family has a
// published version.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<OfficeHoursJobs>("crm");
});

using var host = builder.Build();
await host.StartAsync();

// The cron ticks every quarter of an hour, day and night; the schedule's next occurrence is the first tick inside a
// window. The namespace also lists the system jobs' schedules (alerts, retention), so the read names the job.
var operations = host.Services.GetRequiredService<IActaOperations>();
var schedule = (await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "crm", JobName: "sync-crm"))).Items.Single();
var next = schedule.NextOccurrenceAtUtc!.Value;
var from = new DateTimeOffset(next, TimeSpan.Zero);
var open = (await operations.Calendars.PreviewAsync(officeHours, current, from, count: 1, timeZoneId: "Europe/Ljubljana"))!.Windows[0];
Console.WriteLine($"Next firing {Local(next, "ddd yyyy-MM-dd HH:mm")}; inside office hours: {open.StartUtc <= next && next < open.EndUtc}");

await host.StopAsync();

namespace Acta.Learn.OfficeHours
{
    public readonly record struct SyncCrm;

    public static class Definitions
    {
        // learn: 1. Write the hours as a calendar definition
        // A `weekly-availability` definition opens windows by weekday. `dateOverrides` replaces one date's windows:
        // the Friday before the holiday closes at noon, and `[]` closes the holiday itself. `"timeZone": "schedule"`
        // reads the hours in the zone of the schedule that names the calendar.
        public const string OfficeHours = """
            {
              "kind": "weekly-availability",
              "timeZone": "schedule",
              "weekly": [
                { "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], "windows": [["09:00", "12:00"], ["13:00", "17:00"]] }
              ],
              "dateOverrides": { "2027-10-29": [["09:00", "12:00"]], "2027-11-01": [] }
            }
            """;
        // learn: end
    }

    public sealed class SyncCrmJob
    {
        [Job("sync-crm")]
        // learn: 3. Name the calendar on the schedule
        // `Availability` names the calendar, and `TimeZoneId` is the zone its hours are read in. A tick outside the
        // windows is left out; the next one inside them fires.
        [Schedule("quarter-hourly", "*/15 * * * *", Availability = "office-hours", TimeZoneId = "Europe/Ljubljana")]
        // learn: end
        public Task Handle(SyncCrm input, CancellationToken ct)
        {
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC synced the CRM");
            return Task.CompletedTask;
        }
    }
}
```

