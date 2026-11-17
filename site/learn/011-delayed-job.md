# How to run a job once, later

`ScheduleAsync(input, delay)` stores the job now and makes it due after the delay. No process has to stay alive until then.

- Section: Start jobs, step 7
- Program: `concepts/000-fundamentals/011-delayed-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/011-delayed-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Schedule the job instead of enqueueing it

ScheduleAsync counts the delay on the database clock (db_now + 5s); this host's clock does not affect timing.

The lines that matter:

```csharp
var handle = await jobs.ScheduleAsync(new SendReminder("Sam"), TimeSpan.FromSeconds(5));
```

In `delayed-job.cs` (excerpt):

```csharp
var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.ScheduleAsync(new SendReminder("Sam"), TimeSpan.FromSeconds(5));

Console.WriteLine($"Job {handle.JobRef} enqueued at {DateTime.UtcNow:HH:mm:ss}; runs in ~5s.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");
```

## 2. The job it acts on

`delayed-job.cs` (excerpt):

```csharp
public sealed record SendReminder(string Name);

public static class ReminderJob
{
    [Job("send-reminder")]
    public static void Handle(SendReminder input) =>
        Console.WriteLine($"Reminder fired for {input.Name} at {DateTime.UtcNow:HH:mm:ss}.");
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/000-fundamentals/011-delayed-job
```

Its output must contain:

```text
Reminder fired for Sam at
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `ScheduleAsync(input, DateTimeOffset)` runs the job at an instant instead of after a delay. Work that recurs is a `[Schedule]`, not a job that schedules itself.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to schedule, sleep, and reschedule to an exact instant](/learn/217-absolute-time-controls.md)

## More

- [The whole program](/learn/011-delayed-job.program.md): every file as compiled, 43 lines.
