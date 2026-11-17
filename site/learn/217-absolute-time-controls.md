# How to schedule, sleep, and reschedule to an exact instant

Pin work to an instant instead of a delay: `ScheduleAsync(input, runAt)` starts a job then, `SleepUntilAsync` pauses a handler until then, and `RescheduleUntilAsync` runs the handler again then.

- Section: Start jobs, step 8
- Program: `learn/200-durable-execution/217-absolute-time-controls` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/217-absolute-time-controls
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Start a job at an instant

Pin the earliest claim to an absolute wall-clock instant rather than a relative delay. This demo uses a two-second offset so the run stays fast.

The lines that matter:

```csharp
var runAt = now.AddSeconds(2);
Console.WriteLine($"Part 1 - ScheduleAsync: enqueuing GenerateReport to run at {runAt:HH:mm:ss.fff} UTC");
var report = await jobs.ScheduleAsync(new GenerateReport("q1", SleepUntil: now.AddSeconds(4)), runAt);
```

In `absolute-time-controls.cs` (excerpt):

```csharp
using var host = builder.Build();
await host.StartAsync();

var jobs = host.Services.GetRequiredService<IJobs>();
var now = DateTimeOffset.UtcNow;

var runAt = now.AddSeconds(2);
Console.WriteLine($"Part 1 - ScheduleAsync: enqueuing GenerateReport to run at {runAt:HH:mm:ss.fff} UTC");
var report = await jobs.ScheduleAsync(new GenerateReport("q1", SleepUntil: now.AddSeconds(4)), runAt);
Console.WriteLine($"  job {report.JobRef} is Ready, due later; worker will not claim it before {runAt:HH:mm:ss.fff}");

// --- Part 2: RescheduleUntilAsync ---
// The polling job starts immediately, parks itself until a specific instant on the first run,
// then observes readiness and completes on the second run.
var rescheduleAt = now.AddSeconds(2);
```

## 2. Pause a handler until an instant

SleepUntilAsync arms a named durable timer at an absolute instant; on replay after the instant passes the named timer is already consumed and the handler continues.

The lines that matter:

```csharp
Console.WriteLine($"[generate-report] sleeping until absolute instant {input.SleepUntil:HH:mm:ss.fff} UTC");
await context.SleepUntilAsync("report-delay", input.SleepUntil, ct: ct);
```

In `absolute-time-controls.cs` (excerpt):

```csharp
public sealed class ReportJob
{
    [Job("generate-report")]
    public async Task Handle(GenerateReport input, JobContext context, CancellationToken ct)
    {
        Console.WriteLine($"[generate-report] claimed at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

        Console.WriteLine($"[generate-report] sleeping until absolute instant {input.SleepUntil:HH:mm:ss.fff} UTC");
        await context.SleepUntilAsync("report-delay", input.SleepUntil, ct: ct);

        Console.WriteLine($"[generate-report] resumed at {DateTime.UtcNow:HH:mm:ss.fff} UTC; report for {input.Period} sent");
    }
}
```

## 3. Run the handler again at an instant, without spending a retry

RescheduleUntilAsync re-arms the job to an absolute instant without burning the retry budget; the handler restarts from the top on the next claim.

The lines that matter:

```csharp
Console.WriteLine($"[poll-external] not ready; rescheduling to absolute instant {input.RescheduleUntil:HH:mm:ss.fff} UTC");
await context.RescheduleUntilAsync(input.RescheduleUntil, "external batch not ready", ct);
```

In `absolute-time-controls.cs` (excerpt):

```csharp
public sealed class PollExternalJob
{
    private static int _runs;

    [Job("poll-external")]
    public async Task Handle(PollExternal input, JobContext context, CancellationToken ct)
    {
        var run = Interlocked.Increment(ref _runs);
        Console.WriteLine($"[poll-external] run #{run} at {DateTime.UtcNow:HH:mm:ss.fff} UTC");

        if (run == 1)
        {
            Console.WriteLine($"[poll-external] not ready; rescheduling to absolute instant {input.RescheduleUntil:HH:mm:ss.fff} UTC");
            await context.RescheduleUntilAsync(input.RescheduleUntil, "external batch not ready", ct);
        }

        Console.WriteLine($"[poll-external] batch {input.BatchId} is ready; processing complete");
    }
}
```

## 4. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project learn/200-durable-execution/217-absolute-time-controls
```

Its output must contain:

```text
[poll-external] batch batch-42 is ready; processing complete
report for q1 sent
GenerateReport final status: Succeeded
PollExternal final status:   Succeeded
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` is a helper of the Acta repository (`support/`), used so every program runs on any of the three databases. Do not copy it into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to keep a repeated request from creating a second job](/learn/010-deduplication-key.md)

## More

- [The whole program](/learn/217-absolute-time-controls.program.md): every file as compiled, 99 lines.
