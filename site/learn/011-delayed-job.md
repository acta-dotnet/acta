# How to run a job once, later

`ScheduleAsync(input, delay)` stores the job now and makes it due after the delay. No process has to stay alive until then.

- Section: Start jobs, step 7
- Program: `learn/000-fundamentals/011-delayed-job` in https://github.com/acta-dotnet/acta (one compiled project)
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
var builder = Host.CreateApplicationBuilder(args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<DelayedJobJobs>("delayed-job");
});

using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();

var handle = await jobs.ScheduleAsync(new SendReminder("Sam"), TimeSpan.FromSeconds(5));
Console.WriteLine($"Job {handle.JobRef} enqueued at {DateTime.UtcNow:HH:mm:ss}; runs in ~5s.");
Console.WriteLine("Worker is running - press Ctrl+C to stop.");

await host.WaitForShutdownAsync();
await host.StopAsync();
```

## 2. The job it acts on

`delayed-job.cs` (excerpt):

```csharp
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
dotnet run --project learn/000-fundamentals/011-delayed-job
```

Its output must contain:

```text
Reminder fired for Sam at
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- `ScheduleAsync(input, DateTimeOffset)` runs the job at an instant instead of after a delay. Work that recurs is a `[Schedule]`, not a job that schedules itself.
- A delayed job can sit in the database for days before it runs. The name in `[Job("send-reminder")]` and the namespace in `Run<DelayedJobJobs>("delayed-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.
- `SendReminder` is stored with each job as its input. Add a field as optional or with a default. A renamed, removed or retyped field needs a new job name such as `send-reminder-v2`, and the old handler stays registered until its jobs have finished: [Change a job's input without breaking the jobs already stored](/learn/502-job-versions.md).

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to schedule, sleep, and reschedule to an exact instant](/learn/217-absolute-time-controls.md)

## More

- [The whole program](/learn/011-delayed-job.program.md): every file as compiled, 43 lines.
