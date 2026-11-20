# How to give one job several schedules

Several `[Schedule]` attributes on one job share one permanent job, and schedules that come due together fire it once.

- Section: Run work on a schedule, step 4
- Program: `learn/100-scheduling/103-multiple-schedules` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/103-multiple-schedules
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Put several schedules on the one job

Several [Schedule]s share one permanent job; schedules due at the same instant coalesce into ONE execution, and context.TriggeringSchedules reports which fired.

The lines that matter:

```csharp
[Schedule("fast", "PT10S")]
[Schedule("faster", "PT5S")]
```

In `multiple-schedules.cs` (excerpt):

```csharp
public sealed class ReportJob
{
    [Job("report", EventRecording = EventRecordingCode.Audit)]
    [Schedule("fast", "PT10S")]
    [Schedule("faster", "PT5S")]
    public Task Handle(Report input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine(
            $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
        );
        return Task.CompletedTask;
    }
}
```

## 2. Read which schedules fired this execution

The lines that matter:

```csharp
Console.WriteLine(
    $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
);
```

In `multiple-schedules.cs` (excerpt):

```csharp
public sealed class ReportJob
{
    [Job("report", EventRecording = EventRecordingCode.Audit)]
    [Schedule("fast", "PT10S")]
    [Schedule("faster", "PT5S")]
    public Task Handle(Report input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine(
            $"{DateTime.UtcNow:HH:mm:ss} UTC fired by [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}]"
        );
        return Task.CompletedTask;
    }
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/103-multiple-schedules
```

Its output must contain:

```text
fired by [faster]
fired by [fast, faster]
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("report")]` and the namespace in `Run<MultipleSchedulesJobs>("multiple-schedules")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to decide what happens to runs a schedule missed](/learn/106-schedule-misfire.md)

## More

- [The whole program and its notes](/learn/103-multiple-schedules.program.md): every file as compiled, 88 lines, and the notes on the design, the rows to inspect, and how to break it.
