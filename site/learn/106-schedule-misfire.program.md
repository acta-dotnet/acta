# The whole program: decide what happens to runs a schedule missed

The how-to is [/learn/106-schedule-misfire.md](/learn/106-schedule-misfire.md). This file holds every source file of `learn/100-scheduling/106-schedule-misfire` as it is compiled, and the notes kept beside the code.

## Not in the packages

`UseLocalDatabase` and `LearnLab` are helpers of the Acta repository (`support/`), used so every program runs on SQLite, PostgreSQL or SQL Server and prints its rows. Do not copy them into an application: register the database with `UseSqlite`, `UsePostgres` or `UseSqlServer`, as in [how to run your first job](/learn/001-hello-acta.md). The rows a lab prints come from SQL views, which are the operator's contract; application code reads the same facts through `IJobs` and `IActaOperations`.

## The whole program

### schedule-misfire.cs

```csharp
// How to decide what happens to runs a schedule missed
// Missed occurrences are a business decision, and `MisfireStrategy` on the schedule makes it. Three schedules miss
// the same stretch while paused: `Skip`, the default, passes over them and fires only a latest one less than a minute
// overdue, `CatchUpOnce` runs one firing that covers them all, and `CatchUpAll` runs every one in order.
using System.Collections.Concurrent;
using Acta;
using Acta.Labs;
using Acta.Learn.ScheduleMisfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "schedule-misfire";
string[] jobNames = ["skip-report", "catch-up-once-report", "catch-up-all-report"];
var lookups = jobNames.Select(name => new ScheduleLookup(jobNamespace, name, "every-second")).ToArray();

var builder = Host.CreateApplicationBuilder(args);
var lab = new LearnLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScheduleMisfireJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();

var operations = host.Services.GetRequiredService<IActaOperations>();
var schedules = operations.Schedules;

// The development start deploys the three schedules before the worker starts, so their rows exist once StartAsync returns.
Console.WriteLine("Pausing all three schedules, then letting about three occurrences pass...");
foreach (var lookup in lookups)
{
    await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "misfire demo");
}
Firings.Clear();
await Task.Delay(3500);
var overdue = (await ListAsync(default)).ToDictionary(s => s.JobName, s => s.NextOccurrenceAtUtc!.Value);
await lab.ShowAllAsync(
    "Explore the complete schedule records: paused, with every cursor in the past",
    """
    SELECT *
    FROM schedules_view
    WHERE namespace_name = @jobNamespace
    ORDER BY definition_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "Paused, with every cursor in the past",
    """
    SELECT definition_name, schedule_status, misfire_strategy, next_occurrence_at_utc
    FROM schedules_view
    WHERE namespace_name = @jobNamespace
      AND definition_name IN ('skip-report', 'catch-up-once-report', 'catch-up-all-report')
    ORDER BY definition_name
    """,
    new { jobNamespace }
);

// A resume keeps the cursor: the decision about the missed stretch belongs to the next admission pass.
var since = (await operations.Ledger.ListEventsAsync(new ListEventsQuery(JobNamespace: jobNamespace, PageSize: 1))).Items[0].EventId;
Console.WriteLine("Resuming all three: each cursor stays where it was, and the misfire policy decides at the next pass...");
foreach (var lookup in lookups)
{
    var resumed = await schedules.ResumeAsync(lookup, reasonMessage: "misfire demo");
    Console.WriteLine($"  {lookup.JobName, -22} cursor kept: {resumed.NextOccurrenceAtUtc == overdue[lookup.JobName]}");
}

// The resume instant by the database's clock, which decides every occurrence; the host's clock decides none.
var resumedAt = (
    await operations.Ledger.ListEventsAsync(
        new ListEventsQuery(JobNamespace: jobNamespace, EventCode: EventCode.ScheduleResumed, PageSize: 1)
    )
)
    .Items[0]
    .CreatedAtUtc;
var missed = (int)((resumedAt - overdue["catch-up-all-report"]).TotalSeconds) + 1;

// Wait until every policy has decided the stretch: Skip's cursor has moved past it, CatchUpOnce has run its one
// firing, and CatchUpAll has run one firing per missed occurrence.
using var catchUpTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while (
    (await ListAsync(catchUpTimeout.Token)).Single(s => s.JobName == "skip-report").NextOccurrenceAtUtc <= resumedAt
    || Firings.Of("catch-up-once-report", resumedAt).Count == 0
    || Firings.Of("catch-up-all-report", resumedAt).Count < missed
)
{
    await Task.Delay(100, catchUpTimeout.Token);
}
foreach (var lookup in lookups)
{
    await schedules.PauseAsync(lookup, untilUtc: null, reasonMessage: "misfire demo done");
}

Console.WriteLine();
Console.WriteLine($"--- About {missed} occurrences were missed while paused ---");
var skipped = Firings.Of("skip-report", resumedAt);
var once = Firings.Of("catch-up-once-report", resumedAt);
var all = Firings.Of("catch-up-all-report", resumedAt);
Console.WriteLine($"skip-report            ran {skipped.Count} of them (at most the latest); the rest were skipped");
Console.WriteLine(
    $"catch-up-once-report   ran {once.Count} firing, covering {once[0].Occurrence:HH:mm:ss.fff} through {once[0].CoveredThrough:HH:mm:ss.fff}"
);
Console.WriteLine($"catch-up-all-report    ran {all.Count} firings, one per missed occurrence, in order: {Ordered(all)}");

await lab.ShowAsync(
    "Each policy's first decision after the resume: Skip's skipped range, CatchUpOnce's covering firing, CatchUpAll's first",
    """
    SELECT definition_name, event, details_text
    FROM events_view
    WHERE event_id IN (
        SELECT MIN(event_id)
        FROM events_view
        WHERE namespace_name = @jobNamespace
          AND event_id > @since
          AND event IN ('schedule.admitted', 'schedule.skipped')
        GROUP BY definition_name, event
    )
    ORDER BY definition_name, event_id
    """,
    new { jobNamespace, since }
);

await host.StopAsync();

async Task<IReadOnlyList<ListSchedulesItem>> ListAsync(CancellationToken ct) =>
    (await schedules.ListAsync(new ListSchedulesQuery(JobNamespace: jobNamespace, PageSize: 10), ct))
        .Items.Where(s => jobNames.Contains(s.JobName))
        .ToList();

static bool Ordered(IReadOnlyList<(DateTime Occurrence, DateTime CoveredThrough)> firings) =>
    firings.Zip(firings.Skip(1)).All(pair => pair.First.Occurrence < pair.Second.Occurrence);

namespace Acta.Learn.ScheduleMisfire
{
    /// <summary>The occurrences each job's firings stood for, as the handler read them from its context.</summary>
    public static class Firings
    {
        private static readonly ConcurrentQueue<(string Job, DateTime Occurrence, DateTime CoveredThrough)> Seen = new();

        public static void Record(JobContext ctx)
        {
            // learn: 2. Read which occurrences a firing stands for
            // A catch-up firing can cover a stretch: each triggering schedule gives its occurrence and the instant it
            // covers through.
            foreach (var schedule in ctx.TriggeringSchedules)
            {
                Seen.Enqueue((ctx.JobName, schedule.OccurrenceAtUtc, schedule.CoveredThroughUtc));
            }
            // learn: end
        }

        public static void Clear() => Seen.Clear();

        /// <summary>The job's firings for occurrences at or before <paramref name="through"/>, in the order they ran.</summary>
        public static IReadOnlyList<(DateTime Occurrence, DateTime CoveredThrough)> Of(string job, DateTime through) =>
            Seen.Where(f => f.Job == job && f.Occurrence <= through).Select(f => (f.Occurrence, f.CoveredThrough)).ToList();
    }

    public sealed class MisfireJobs
    {
        // learn: 1. Choose the misfire strategy on the schedule
        // `MisfireStrategy` says what the next admission pass does with the occurrences that came due while the
        // schedule could not fire.
        [Job("skip-report")]
        [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.Skip)]
        public Task Skip(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

        [Job("catch-up-once-report")]
        [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpOnce)]
        public Task CatchUpOnce(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

        [Job("catch-up-all-report")]
        [Schedule("every-second", "PT1S", MisfireStrategy = MisfireStrategyCode.CatchUpAll)]
        public Task CatchUpAll(JobContext ctx, CancellationToken ct) => Run(ctx, ct);

        // learn: end

        private static async Task Run(JobContext ctx, CancellationToken ct)
        {
            Firings.Record(ctx);
            await Task.Delay(50, ct);
        }
    }
}
```

## Notes

### The problem

After downtime or a pause, a schedule cursor points into the past. "Run everything missed," "run once now,"
and "skip ahead" have different business meanings and operational costs.

### Common approaches

- Skip missed instants and move to the next future occurrence.
- Catch up once, coalescing the outage into one recovery execution.
- Run every missed occurrence, in order, as its own execution.
- Materialize every occurrence as independently tracked work.

### Why this design

Each schedule names its policy, and `Skip` is the default when it names none. A resume, like a deploy
or a worker start, decides nothing: it keeps the cursor where it was, and the next admission pass applies the policy
to the occurrences it finds already past. An occurrence less than a minute late still counts as on time.

- `Skip` skips the missed occurrences, except the latest when it is within that minute.
- `CatchUpOnce` runs one firing whose occurrence is the earliest missed and whose covered-through is the
  latest, so the handler knows the stretch it stands for.
- `CatchUpLatest` runs the latest however late it is and skips the earlier ones.
- `CatchUpAll` runs every missed occurrence in order, one firing per settlement.

`[Schedule(ExpireAfter = "1h", ExpireAction = ...)]` bounds how old an occurrence may be before any of these
apply: older ones are skipped with the reason `expired`, and `Pause` also pauses the schedule.

### Trade-offs

`Skip` loses missed work. `CatchUpOnce` keeps the stretch but not one execution per occurrence.
`CatchUpAll` keeps every occurrence but runs them one after another on one permanent job, so a long outage
becomes a long backlog; with `ScheduledDelivery = PerOccurrence` each one is a root job of its own.

### Run the experiment

```bash
dotnet run --project learn/100-scheduling/106-schedule-misfire
```

The program pauses three schedules that fire every second, lets about three occurrences pass, resumes
them, and waits until each policy has decided the stretch. It prints how many firings each ran for the
missed occurrences: `skip-report` at most one, `catch-up-once-report` one covering the stretch, and
`catch-up-all-report` one per missed occurrence, in order.

### Rows to inspect

Run with `--all-columns` to execute the visible `SELECT *` Explore query first. The default Notice
queries select the fields that prove the lesson, and the text below explains their meaning.

`schedules_view` shows the three cursors in the past while the schedules are paused. After the resume,
`events_view` shows each policy's first decision: a `schedule.skipped` range for `skip-report` beside its
admission of the latest, one `schedule.admitted` for `catch-up-once-report` whose details carry the
covered-through instant, and the first of `catch-up-all-report`'s admissions.

### Break it

Pause these one-second schedules for as long as you like: `Skip` still runs the latest occurrence, since
one fell due less than a second before the resume and the grace is a minute. Give a schedule occurrences
more than a minute apart and resume it more than a minute after the one it missed, and `Skip` runs none of
them, while `CatchUpLatest` would still run that latest one.

### When not to use

Use `Skip` for cache refresh, telemetry, and cleanup. Use `CatchUpOnce` for reconciliation or summary work
where one recovery pass matters. Use `CatchUpAll`, or a per-occurrence definition, for accounting or
financial occurrences that must never coalesce.

### Source trail

- [The related Engineering Lab](https://github.com/acta-dotnet/acta/blob/main/docs/engineering-labs.md)
- [`schedule-misfire.cs`](https://github.com/acta-dotnet/acta/blob/main/learn/100-scheduling/106-schedule-misfire/schedule-misfire.cs)
- [`ScheduleEvaluator.cs`](https://github.com/acta-dotnet/acta/blob/main/src/Acta/Domain/Schedules/ScheduleEvaluator.cs)
