# How to pause and resume a schedule

`operations.Schedules` pauses a schedule from outside the handler, for good or until an instant, and resumes it.

- Section: Run work on a schedule, step 6
- Program: `learn/100-scheduling/105-schedule-control` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/105-schedule-control
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Name the schedule

A schedule is named by its namespace, its job, and its own name. The development start deploys the schedule before the worker starts, so its row exists once StartAsync returns.

The lines that matter:

```csharp
var lookup = new ScheduleLookup("schedule-control", "hourly-report", "every-hour");
```

In `schedule-control.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var schedules = host.Services.GetRequiredService<IActaOperations>().Schedules;
var queries = host.Services.GetRequiredService<IActaOperations>();

var lookup = new ScheduleLookup("schedule-control", "hourly-report", "every-hour");

Console.WriteLine("Pausing schedule indefinitely...");

var r1 = await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "maintenance window");
Console.WriteLine($"After indefinite pause: status={r1.Status} next-run={r1.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");
```

## 2. Pause it until someone resumes it

An indefinite pause: the schedule does not fire until an operator explicitly resumes it.

The lines that matter:

```csharp
var r1 = await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "maintenance window");
```

In `schedule-control.cs` (excerpt):

```csharp
var lookup = new ScheduleLookup("schedule-control", "hourly-report", "every-hour");

Console.WriteLine("Pausing schedule indefinitely...");

var r1 = await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "maintenance window");
Console.WriteLine($"After indefinite pause: status={r1.Status} next-run={r1.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

var resumeAt = DateTime.UtcNow.AddHours(2);
Console.WriteLine($"Replacing with timed pause until {resumeAt:O}...");
var r2 = await schedules.PauseAsync(lookup, untilUtc: resumeAt, reasonMessage: "drain window");
Console.WriteLine($"After timed pause: status={r2.Status} paused-until={r2.PausedUntilUtc?.ToString("O") ?? "(none)"}");
```

## 3. Or pause it until an instant

A timed pause: the scheduler resumes the schedule by itself when the timestamp passes.

The lines that matter:

```csharp
var resumeAt = DateTime.UtcNow.AddHours(2);
Console.WriteLine($"Replacing with timed pause until {resumeAt:O}...");
var r2 = await schedules.PauseAsync(lookup, untilUtc: resumeAt, reasonMessage: "drain window");
```

In `schedule-control.cs` (excerpt):

```csharp
Console.WriteLine("Pausing schedule indefinitely...");

var r1 = await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "maintenance window");
Console.WriteLine($"After indefinite pause: status={r1.Status} next-run={r1.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

var resumeAt = DateTime.UtcNow.AddHours(2);
Console.WriteLine($"Replacing with timed pause until {resumeAt:O}...");
var r2 = await schedules.PauseAsync(lookup, untilUtc: resumeAt, reasonMessage: "drain window");
Console.WriteLine($"After timed pause: status={r2.Status} paused-until={r2.PausedUntilUtc?.ToString("O") ?? "(none)"}");

Console.WriteLine("Resuming schedule...");

var r3 = await schedules.ResumeAsync(lookup, reasonMessage: "drain complete");
Console.WriteLine($"After resume: status={r3.Status} next-run={r3.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");
```

## 4. Resume it

A resume clears the pause and keeps the cursor; the next admission pass decides any missed occurrence.

The lines that matter:

```csharp
var r3 = await schedules.ResumeAsync(lookup, reasonMessage: "drain complete");
```

In `schedule-control.cs` (excerpt):

```csharp
Console.WriteLine("Resuming schedule...");

var r3 = await schedules.ResumeAsync(lookup, reasonMessage: "drain complete");
Console.WriteLine($"After resume: status={r3.Status} next-run={r3.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");

// Confirm via the query surface that the schedule is active again and has a next-run time.
var page = await queries.Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: "schedule-control"));
var item = page.Items.FirstOrDefault(i => i.ScheduleName == "every-hour");
Console.WriteLine($"Query confirms: status={item?.Status} next-run={item?.NextOccurrenceAtUtc?.ToString("O") ?? "(none)"}");
```

## 5. The job it acts on

`schedule-control.cs` (excerpt):

```csharp
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
```

## 6. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/105-schedule-control
```

Its output must contain:

```text
After indefinite pause: status=Paused
After timed pause: status=Paused paused-until=
After resume: status=Active
Query confirms: status=Active
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("hourly-report")]` and the namespace in `Run<ScheduleControlJobs>("schedule-control")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to drive a schedule from a published calendar](/learn/108-calendar-catalog.md)

## More

- [The whole program](/learn/105-schedule-control.program.md): every file as compiled, 78 lines.
