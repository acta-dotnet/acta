# How to skip public holidays on a daily schedule

A cron cannot name the days a country takes off. An occurrence calendar can: local times on the weekdays you list, minus the dates you exclude. The list is data an operator publishes, so next year's holidays need no deploy.

- Section: Run work on a schedule, step 10
- Program: `learn/100-scheduling/114-holiday-calendar` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/114-holiday-calendar
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. List the holidays in a calendar definition

A `daily-local-time` definition fires at each of `times` on each of `days`, and skips every date in `excludedDates`. Paste the whole list of holidays: one that falls on a weekend is skipped twice over.

The lines that matter:

```csharp
public const string WorkingDays = """
    {
      "kind": "daily-local-time",
      "times": ["08:00"],
      "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
      "excludedDates": ["2027-11-01", "2027-12-25", "2027-12-26", "2028-01-01", "2028-01-02", "2028-02-08"]
    }
    """;
```

In `holiday-calendar.cs` (excerpt):

```csharp
public static class Definitions
{
    public const string WorkingDays = """
        {
          "kind": "daily-local-time",
          "times": ["08:00"],
          "days": ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"],
          "excludedDates": ["2027-11-01", "2027-12-25", "2027-12-26", "2028-01-01", "2028-01-02", "2028-02-08"]
        }
        """;
}
```

## 2. Preview the draft, then publish it

The preview answers the draft's next occurrences from any instant, in the zone you name. This one starts on the Friday before a Monday holiday. When the list changes, save the new definition and publish it again: every schedule that names the calendar follows the new version.

The lines that matter:

```csharp
var draft = await calendars.SaveDraftAsync(workingDays, CalendarRoleCode.Occurrence, Definitions.WorkingDays);
var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(workingDays, draft.Version!.Value, friday, count: 3, timeZoneId: "Europe/Ljubljana"))!;
foreach (var occurrence in preview.OccurrencesUtc)
{
    Console.WriteLine($"reports {Local(occurrence)}");
}

await calendars.PublishAsync(workingDays, draft.Version.Value, reasonMessage: "Holidays through February 2028");
```

In `holiday-calendar.cs` (excerpt):

```csharp
var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;

var draft = await calendars.SaveDraftAsync(workingDays, CalendarRoleCode.Occurrence, Definitions.WorkingDays);
var friday = new DateTimeOffset(2027, 10, 29, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(workingDays, draft.Version!.Value, friday, count: 3, timeZoneId: "Europe/Ljubljana"))!;
foreach (var occurrence in preview.OccurrencesUtc)
{
    Console.WriteLine($"reports {Local(occurrence)}");
}

await calendars.PublishAsync(workingDays, draft.Version.Value, reasonMessage: "Holidays through February 2028");
current = draft.Version.Value;

await seedHost.StopAsync();
```

## 3. Name the calendar on the schedule

`Calendar` takes the place of a cron or an interval, and `TimeZoneId` is the zone the calendar's local times are read in.

The lines that matter:

```csharp
[Schedule("working-days-at-eight", Calendar = "working-days", TimeZoneId = "Europe/Ljubljana")]
```

In `holiday-calendar.cs` (excerpt):

```csharp
public sealed class DailyReportJob
{
    [Job("daily-report")]
    [Schedule("working-days-at-eight", Calendar = "working-days", TimeZoneId = "Europe/Ljubljana")]
    public Task Handle(DailyReport input, CancellationToken ct)
    {
        Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd} sent the daily report");
        return Task.CompletedTask;
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/114-holiday-calendar
```

Its output must contain:

```text
reports Fri 2027-10-29 08:00
reports Tue 2027-11-02 08:00
reports Wed 2027-11-03 08:00
the calendar's next instant: True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("daily-report")]` and the namespace in `Run<HolidayCalendarJobs>("reports")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to make every occurrence its own job](/learn/110-per-occurrence-delivery.md)

## More

- [The whole program](/learn/114-holiday-calendar.program.md): every file as compiled, 107 lines.
