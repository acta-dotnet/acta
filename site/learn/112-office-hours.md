# How to keep a schedule inside office hours

An availability calendar is the open time a schedule may fire in: windows by weekday, with a date closed or given other hours. A schedule names it, and an occurrence outside every window is left out, never moved.

- Section: Run work on a schedule, step 8
- Program: `learn/100-scheduling/112-office-hours` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/112-office-hours
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the hours as a calendar definition

A `weekly-availability` definition opens windows by weekday. `dateOverrides` replaces one date's windows: the Friday before the holiday closes at noon, and `[]` closes the holiday itself. `"timeZone": "schedule"` reads the hours in the zone of the schedule that names the calendar.

The lines that matter:

```csharp
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
```

In `office-hours.cs` (excerpt):

```csharp
public static class Definitions
{
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
}
```

## 2. Preview the draft, then publish it

The preview answers the draft's next windows from any instant, in the zone you name, so the hours can be read before a schedule follows them. This one starts on the Friday before a public holiday.

The lines that matter:

```csharp
var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OfficeHours);
var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(officeHours, draft.Version!.Value, friday, count: 5, timeZoneId: "Europe/Ljubljana"))!;
foreach (var window in preview.Windows)
{
    Console.WriteLine($"open {Local(window.StartUtc, "ddd yyyy-MM-dd HH:mm")} to {Local(window.EndUtc!.Value, "HH:mm")}");
}

await calendars.PublishAsync(officeHours, draft.Version.Value, reasonMessage: "Office hours");
```

In `office-hours.cs` (excerpt):

```csharp
var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;

var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OfficeHours);
var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(officeHours, draft.Version!.Value, friday, count: 5, timeZoneId: "Europe/Ljubljana"))!;
foreach (var window in preview.Windows)
{
    Console.WriteLine($"open {Local(window.StartUtc, "ddd yyyy-MM-dd HH:mm")} to {Local(window.EndUtc!.Value, "HH:mm")}");
}

await calendars.PublishAsync(officeHours, draft.Version.Value, reasonMessage: "Office hours");
current = draft.Version.Value;

await seedHost.StopAsync();
```

## 3. Name the calendar on the schedule

`Availability` names the calendar, and `TimeZoneId` is the zone its hours are read in. A tick outside the windows is left out; the next one inside them fires.

The lines that matter:

```csharp
[Schedule("quarter-hourly", "*/15 * * * *", Availability = "office-hours", TimeZoneId = "Europe/Ljubljana")]
```

In `office-hours.cs` (excerpt):

```csharp
public sealed class SyncCrmJob
{
    [Job("sync-crm")]
    [Schedule("quarter-hourly", "*/15 * * * *", Availability = "office-hours", TimeZoneId = "Europe/Ljubljana")]
    public Task Handle(SyncCrm input, CancellationToken ct)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC synced the CRM");
        return Task.CompletedTask;
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/112-office-hours
```

Its output must contain:

```text
open Fri 2027-10-29 09:00 to 12:00
open Tue 2027-11-02 09:00 to 12:00
open Tue 2027-11-02 13:00 to 17:00
inside office hours: True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("sync-crm")]` and the namespace in `Run<OfficeHoursJobs>("crm")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run a job on the last business day of each month](/learn/113-business-day-schedule.md)

## More

- [The whole program](/learn/112-office-hours.program.md): every file as compiled, 105 lines.
