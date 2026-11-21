# How to run a job on a repeating schedule

`[Schedule]` with a cron makes one permanent job that fires on that cadence, with nothing to enqueue.

- Section: Run work on a schedule, step 1
- Program: `learn/100-scheduling/101-recurring-job` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows; "Remember" below carries the rules that bite this task.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/101-recurring-job
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Put a schedule on the job

`[Schedule]` takes a name and a cron, here one of the `Cron` constants, read in UTC unless `TimeZoneId` says otherwise. One permanent job fires on the cadence, and the count each firing returns replaces the result the last one stored. `EventRecording = Failures` keeps a frequent schedule from writing an event for every firing that succeeds, and the build warns when a schedule faster than once a minute leaves it out. Nothing enqueues a scheduled job, so its input is an empty record.

The lines that matter:

```csharp
[Job("purge-stale-sessions", EventRecording = EventRecordingCode.Failures)]
[Schedule("every-15-seconds", Cron.Every15Seconds)]
```

In `recurring-job.cs` (excerpt):

```csharp
public sealed class PurgeStaleSessionsJob
{
    [Job("purge-stale-sessions", EventRecording = EventRecordingCode.Failures)]
    [Schedule("every-15-seconds", Cron.Every15Seconds)]
    public async Task<int> Handle(PurgeStaleSessions input, JobContext context, CancellationToken ct)
    {
        await Task.Delay(100, ct);
        var purged = Random.Shared.Next(0, 25);
        Console.WriteLine(
            $"{DateTime.UtcNow:HH:mm:ss} UTC [{string.Join(", ", context.TriggeringSchedules.Select(s => s.ScheduleName))}] purged {purged} stale sessions"
        );
        return purged;
    }
}
```

## 2. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/100-scheduling/101-recurring-job
```

Its output must contain:

```text
[every-15-seconds] purged
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Remember

- A schedule is written by a deploy: `jobs migrate` or `jobs deploy` in production, the development start on your own machine.
- A permanent job keeps its step results from one firing to the next, so a step that succeeded answers the next firing from its record. End the handler with `await ctx.ResetStateAsync(ct)`, as its last action, and each firing starts clean. A step named per firing, `$"send.f{ctx.FiringId:N}"`, also runs every time, but each firing then leaves a step row that is kept as long as the job, and a permanent job lives as long as its schedule.
- The handler does its bounded work and returns. Never loop inside it: the schedule brings the next firing.
- A job on a schedule runs across every deploy, for as long as the application lives. The name in `[Job("purge-stale-sessions")]` and the namespace in `Run<RecurringJobs>("recurring-job")` are stored with every job. Treat both as permanent once jobs exist under them: a job stored under a name no handler carries stays Ready, and nothing runs it.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to run a job every day at a set hour in your time zone](/learn/104-timezone-schedule.md)

## More

- [The whole program](/learn/101-recurring-job.program.md): every file as compiled, 43 lines.
