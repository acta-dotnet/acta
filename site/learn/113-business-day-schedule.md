# How to run a job on the last business day of each month

An occurrence calendar is the list of instants a schedule fires at, where a cron cannot say them: the last business day of a month, minus the holidays you name. The schedule takes its occurrences from the calendar.

- Section: Run work on a schedule, step 9
- Program: `learn/100-scheduling/113-business-day-schedule` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/113-business-day-schedule
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Write the rule as a calendar definition

A `business-day` definition picks the first or last business day of each week, month, quarter or year, at a local time. Business days are Monday to Friday unless `businessDays` says otherwise, and a date in `holidays` is not one.

The lines that matter:

```csharp
public const string SettlementDays = """
    {
      "kind": "business-day",
      "select": "last",
      "period": "month",
      "time": "16:00",
      "holidays": ["2027-12-31"]
    }
    """;
```

In `business-day-schedule.cs` (excerpt):

```csharp
public static class Definitions
{
    public const string SettlementDays = """
        {
          "kind": "business-day",
          "select": "last",
          "period": "month",
          "time": "16:00",
          "holidays": ["2027-12-31"]
        }
        """;
}
```

## 2. Preview the draft, then publish it

The preview answers the draft's next occurrences from any instant, in the zone you name. December 2027 ends on a Friday the definition lists as a holiday, so that month settles a day earlier.

The lines that matter:

```csharp
var draft = await calendars.SaveDraftAsync(settlementDays, CalendarRoleCode.Occurrence, Definitions.SettlementDays);
var october = new DateTimeOffset(2027, 10, 1, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(settlementDays, draft.Version!.Value, october, count: 3, timeZoneId: "Europe/Ljubljana"))!;
foreach (var occurrence in preview.OccurrencesUtc)
{
    Console.WriteLine($"settles {Local(occurrence)}");
}

await calendars.PublishAsync(settlementDays, draft.Version.Value, reasonMessage: "Settlement days");
```

In `business-day-schedule.cs` (excerpt):

```csharp
var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;

var draft = await calendars.SaveDraftAsync(settlementDays, CalendarRoleCode.Occurrence, Definitions.SettlementDays);
var october = new DateTimeOffset(2027, 10, 1, 0, 0, 0, TimeSpan.Zero);
var preview = (await calendars.PreviewAsync(settlementDays, draft.Version!.Value, october, count: 3, timeZoneId: "Europe/Ljubljana"))!;
foreach (var occurrence in preview.OccurrencesUtc)
{
    Console.WriteLine($"settles {Local(occurrence)}");
}

await calendars.PublishAsync(settlementDays, draft.Version.Value, reasonMessage: "Settlement days");
current = draft.Version.Value;

await seedHost.StopAsync();
```

## 3. Name the calendar on the schedule

`Calendar` takes the place of a cron or an interval, and `TimeZoneId` is the zone the calendar's local time is read in.

The lines that matter:

```csharp
[Schedule("month-end", Calendar = "settlement-days", TimeZoneId = "Europe/Ljubljana")]
```

In `business-day-schedule.cs` (excerpt):

```csharp
public sealed class SettleJob
{
    [Job("settle")]
    [Schedule("month-end", Calendar = "settlement-days", TimeZoneId = "Europe/Ljubljana")]
    public Task Handle(Settle input, CancellationToken ct)
    {
        Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd} settled the month");
        return Task.CompletedTask;
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/113-business-day-schedule
```

Its output must contain:

```text
settles Fri 2027-10-29 16:00
settles Tue 2027-11-30 16:00
settles Thu 2027-12-30 16:00
the calendar's next instant: True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("settle")]` and the namespace in `Run<BusinessDayScheduleJobs>("settlement")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to skip public holidays on a daily schedule](/learn/114-holiday-calendar.md)

## More

- [The whole program](/learn/113-business-day-schedule.program.md): every file as compiled, 106 lines.
