# How to drive a schedule from a published calendar

A calendar is data that an operator publishes through `operations.Calendars`. A schedule binds it by name, and a later publication retimes every schedule bound to it in one transaction while workers run.

- Section: Run work on a schedule, step 7
- Program: `concepts/100-scheduling/108-calendar-catalog` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/108-calendar-catalog
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Save the calendar as a draft and publish it

A deploy step names no versions, so it runs again as it is: a definition the latest version already holds saves nothing and answers AlreadyInState with that version, and publishing the current version again changes nothing.

The lines that matter:

```csharp
var draft = await calendars.SaveDraftAsync(officeHours, CalendarRoleCode.Availability, Definitions.OpenAllDay);
Console.WriteLine($"Draft saved: {draft.Action}, version {draft.Version}");
var published = await calendars.PublishAsync(officeHours, draft.Version!.Value, reasonMessage: "Go live");
```

In `calendar-catalog.cs` (excerpt):

```csharp
// A family in sys is shared: every namespace binds it by its plain name.
var officeHours = new CalendarLookup("sys", "office-hours");

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

// Phase 2: the development start deploys tick, whose schedule names office-hours. The deploy checks that the family
// has a published version, and the name resolves to the shared family's current version afresh at every evaluation.
var builder = Host.CreateApplicationBuilder(args);
builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<CalendarCatalogJobs>("calendar-catalog");
});

using var host = builder.Build();
await host.StartAsync();
```

## 2. Bind the schedule to the calendar by name

`Availability` names the calendar. The schedule fires only inside the windows its current version opens.

The lines that matter:

```csharp
[Schedule("every-10s-in-hours", "PT10S", Availability = "office-hours")]
```

In `calendar-catalog.cs` (excerpt):

```csharp
public readonly record struct Tick;

public static class Definitions
{
    public const string OpenAllDay = """
        {"kind":"weekly-availability","timeZone":"UTC",
         "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],"windows":[["00:00","23:59:59"]]}]}
        """;

    // A date override replaces the weekday's windows for that date; none closes it.
    public static string ClosedOn(string date) =>
        $$$"""
            {"kind":"weekly-availability","timeZone":"UTC",
             "weekly":[{"days":["Monday","Tuesday","Wednesday","Thursday","Friday","Saturday","Sunday"],"windows":[["00:00","23:59:59"]]}],
             "dateOverrides":{"{{{date}}}":[]}}
            """;
}

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

The publication moves the cursor of every schedule bound to the calendar, in the same transaction.

The lines that matter:

```csharp
var result = await operations.Calendars.PublishAsync(officeHours, closed.Version.Value, reasonMessage: $"Closed on {today}");
Console.WriteLine($"Published: {result.Action}; retimed [{string.Join(", ", result.RetimedSchedules)}]");
```

In `calendar-catalog.cs` (excerpt):

```csharp
var operations = host.Services.GetRequiredService<IActaOperations>();
Console.WriteLine("Ticking every 10s inside the open window...");
await Task.Delay(TimeSpan.FromSeconds(25));

// The operator closes today, editing the version they read: naming it as the expected latest version turns another
// operator's save in between into a VersionConflict rather than a draft built on a stale read. The draft is previewed
// beside the current version, then published: the bound schedule's cursor moves to the first instant inside the next window.
var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
var read = (await operations.Calendars.GetAsync(officeHours))!;
var closed = await operations.Calendars.SaveDraftAsync(
    officeHours,
    CalendarRoleCode.Availability,
    Definitions.ClosedOn(today),
    expectedLatestVersion: read.LatestVersion,
    reasonMessage: $"Closed on {today}"
);
var preview = (await operations.Calendars.PreviewAsync(officeHours, closed.Version!.Value, count: 3))!;
Console.WriteLine(
    $"Preview of version {closed.Version}: the next window opens {preview.Windows[0].StartUtc:O}; "
        + $"{preview.SchedulesTotal} bound schedule(s), {preview.OverdueTotal} overdue"
);

var result = await operations.Calendars.PublishAsync(officeHours, closed.Version.Value, reasonMessage: $"Closed on {today}");
Console.WriteLine($"Published: {result.Action}; retimed [{string.Join(", ", result.RetimedSchedules)}]");

// The namespace also lists the system jobs' schedules (alerts, retention), so the read names the job.
var schedule = (
    await operations.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "calendar-catalog", JobName: "tick"))
).Items.Single();
Console.WriteLine($"Next occurrence {schedule.NextOccurrenceAtUtc:O}.");

Console.WriteLine("Watching 15s for a tick that must not come...");
await Task.Delay(TimeSpan.FromSeconds(15));
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/100-scheduling/108-calendar-catalog
```

Its output must contain:

```text
Published: Applied, version 1 is current
 UTC tick
Published: Applied; retimed [calendar-catalog/tick/every-10s-in-hours]
Watching 15s for a tick that must not come...
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to make every occurrence its own job](/learn/110-per-occurrence-delivery.md)

## More

- [The whole program](/learn/108-calendar-catalog.program.md): every file as compiled, 118 lines.
