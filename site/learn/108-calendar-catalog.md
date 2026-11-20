# How to drive a schedule from a published calendar

A calendar is data that an operator publishes through `operations.Calendars`. A schedule binds it by name, and a later publication retimes every schedule bound to it in one transaction while workers run.

- Section: Run work on a schedule, step 7
- Program: `learn/100-scheduling/108-calendar-catalog` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/108-calendar-catalog
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Save the calendar as a draft and publish it

A calendar is a JSON definition saved as a draft, then published. `officeHours` names the family `office-hours` in `sys`, which every namespace binds by its plain name, and `Definitions.OpenAllDay` is a definition open all day, every day. A deploy step names no versions, so it runs again as it is: a definition the latest version already holds saves nothing and answers AlreadyInState with that version, and publishing the current version again changes nothing.

The lines that matter:

```csharp
var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OpenAllDay);
Console.WriteLine($"Draft saved: {draft.Action}, version {draft.Version}");
var published = await calendars.PublishAsync(officeHours, draft.Version!.Value, reasonMessage: "Go live");
```

In `calendar-catalog.cs` (excerpt):

```csharp
// Phase 1: the calendar is published before the job that binds it deploys, as an operator would do it from
// the dashboard. An enqueue-only host (no Run<>) still bootstraps the schema, and the operations need no worker.
{
    var seed = Host.CreateApplicationBuilder(args);
    seed.Services.UseActa(j => j.UseLocalDatabase(seed.Configuration));
    using var seedHost = seed.Build();
    await seedHost.StartAsync();

    var calendars = seedHost.Services.GetRequiredService<IActaOperations>().Calendars;
    var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OpenAllDay);
    Console.WriteLine($"Draft saved: {draft.Action}, version {draft.Version}");
    var published = await calendars.PublishAsync(officeHours, draft.Version!.Value, reasonMessage: "Go live");
    Console.WriteLine($"Published: {published.Action}, version {published.Version} is current");

    await seedHost.StopAsync();
}
```

## 2. Bind the schedule to the calendar by name

`Availability` names the calendar. The schedule fires only inside the windows its current version opens.

The lines that matter:

```csharp
[Schedule("every-10s-in-hours", "PT10S", Availability = "office-hours")]
```

In `calendar-catalog.cs` (excerpt):

```csharp
public sealed class TickJob
{
    [Job("tick")]
    [Schedule("every-10s-in-hours", "PT10S", Availability = "office-hours")]
    public Task Handle(Tick input, CancellationToken ct)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} UTC tick");
        return Task.CompletedTask;
    }
}
```

## 3. Publish a change and let it retime the schedule

The publication moves the cursor of every schedule bound to the calendar, in the same transaction. `closed` is the draft saved just above, whose definition has no window today.

The lines that matter:

```csharp
var result = await operations.Calendars.PublishAsync(officeHours, closed.Version.Value, reasonMessage: $"Closed on {today}");
Console.WriteLine($"Published: {result.Action}; retimed [{string.Join(", ", result.RetimedSchedules)}]");
```

In `calendar-catalog.cs` (excerpt):

```csharp
var result = await operations.Calendars.PublishAsync(officeHours, closed.Version.Value, reasonMessage: $"Closed on {today}");
Console.WriteLine($"Published: {result.Action}; retimed [{string.Join(", ", result.RetimedSchedules)}]");

// The namespace also lists the system jobs' schedules (alerts, retention), so the read names the job.
var schedule = (
    await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "calendar-catalog", JobName: "tick"))
).Items.Single();
Console.WriteLine($"Next occurrence {schedule.NextOccurrenceAtUtc:O}.");
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/108-calendar-catalog
```

Its output must contain:

```text
Published: Applied, version 1 is current
 UTC tick
Published: Applied; retimed [calendar-catalog/tick/every-10s-in-hours]
Watching 15s for a tick that must not come...
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("tick")]` and the namespace in `Run<CalendarCatalogJobs>("calendar-catalog")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep a schedule inside office hours](/learn/112-office-hours.md)

## More

- [The whole program](/learn/108-calendar-catalog.program.md): every file as compiled, 122 lines.
