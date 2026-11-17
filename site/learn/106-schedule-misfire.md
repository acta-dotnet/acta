# How to decide what happens to runs a schedule missed

Missed occurrences are a business decision. Three schedules miss the same stretch while paused, and each one's misfire policy decides it differently: `Skip` runs at most the latest, `CatchUpOnce` runs one firing that covers them all, `CatchUpAll` runs every one in order.

- Section: Run work on a schedule, step 5
- Program: `concepts/100-scheduling/106-schedule-misfire` in https://github.com/acta-dotnet/acta (one compiled project)
- Status: Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.
- Rules: [/llms.txt](/llms.txt) is the contract every handler follows, starting with the handler running again from its first line; read it before you copy code from here.
- Release: the one /llms.txt names at its top.
- Page for people: /learn/106-schedule-misfire
- New to Acta: it is durable execution for .NET, a library with its state in the app's SQL database; the first run is in /llms.txt.

## 1. Choose the misfire strategy on the schedule

`MisfireStrategy` says what the next admission pass does with the occurrences that came due while the schedule could not fire.

The lines that matter:

```csharp
[Job("skip-report")]
[Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.Skip)]
public Task Skip(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

[Job("catch-up-once-report")]
[Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpOnce)]
public Task CatchUpOnce(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

[Job("catch-up-all-report")]
[Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpAll)]
public Task CatchUpAll(JobContext ctx, CancellationToken ct) => Run(ctx, ct);
```

In `schedule-misfire.cs` (excerpt):

```csharp
public sealed class MisfireJobs
{
    [Job("skip-report")]
    [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.Skip)]
    public Task Skip(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

    [Job("catch-up-once-report")]
    [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpOnce)]
    public Task CatchUpOnce(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

    [Job("catch-up-all-report")]
    [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpAll)]
    public Task CatchUpAll(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

    private static async Task Run(JobContext ctx, CancellationToken ct)
    {
        Firings.Record(ctx);
        await Task.Delay(50, ct);
    }
}
```

## 2. Read which occurrences a firing stands for

A catch-up firing can cover a stretch: each triggering schedule gives its occurrence and the instant it covers through.

The lines that matter:

```csharp
foreach (var schedule in ctx.TriggeringSchedules)
{
    Seen.Enqueue((ctx.JobName, schedule.OccurrenceAtUtc, schedule.CoveredThroughUtc));
}
```

In `schedule-misfire.cs` (excerpt):

```csharp
/// <summary>The occurrences each job's firings stood for, as the handler read them from its context.</summary>
public static class Firings
{
    private static readonly ConcurrentQueue<(string Job, DateTime Occurrence, DateTime CoveredThrough)> Seen = new();

    public static void Record(JobContext ctx)
    {
        foreach (var schedule in ctx.TriggeringSchedules)
        {
            Seen.Enqueue((ctx.JobName, schedule.OccurrenceAtUtc, schedule.CoveredThroughUtc));
        }
    }

    public static void Clear() => Seen.Clear();

    /// <summary>The job's firings for occurrences at or before <paramref name="through"/>, in the order they ran.</summary>
    public static IReadOnlyList<(DateTime Occurrence, DateTime CoveredThrough)> Of(string job, DateTime through) =>
        Seen.Where(f => f.Job == job && f.Occurrence <= through).Select(f => (f.Occurrence, f.CoveredThrough)).ToList();
}
```

## 3. Run it and check the output

From a clone of the repository, with the .NET 10 SDK:

```bash
dotnet run --project concepts/100-scheduling/106-schedule-misfire
```

Its output must contain:

```text
cursor kept: True
of them (at most the latest); the rest were skipped
catch-up-once-report   ran 1 firing, covering
one per missed occurrence, in order: True
```

Checked every release. Before each release, Acta Verify runs this program and checks that its output contains these lines.

## Not in the packages

`UseLocalDatabase` and `ConceptLab` are helpers of the Acta repository (`support/`), used so every program runs on any of the three databases and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## Next

- [How to pause and resume a schedule](/learn/105-schedule-control.md)

## More

- [The whole program and its notes](/learn/106-schedule-misfire.program.md): every file as compiled, 187 lines, and the notes on the design, the rows to inspect, and how to break it.
