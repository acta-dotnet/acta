// Concept: missed occurrences are a business decision. Three schedules miss the same stretch while paused; a resume
// keeps each cursor where it was, and the next admission pass applies each schedule's misfire policy: Skip runs at
// most the latest, CatchUpOnce runs one firing that covers them all, CatchUpAll runs every one in order.
using System.Collections.Concurrent;
using Acta;
using Acta.Concepts.ScheduleMisfire;
using Acta.Labs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const string jobNamespace = "schedule-misfire";
string[] jobNames = ["skip-report", "catch-up-once-report", "catch-up-all-report"];
var lookups = jobNames.Select(name => new ScheduleLookup(jobNamespace, name, "every-second")).ToArray();

var builder = Host.CreateApplicationBuilder(args);
var lab = new ConceptLab(builder.Configuration, args);

builder.Services.UseActa(j =>
{
    j.UseLocalDatabase(builder.Configuration);
    j.Run<ScheduleMisfireJobs>(jobNamespace);
});

using var host = builder.Build();
await host.StartAsync();

var operations = host.Services.GetRequiredService<IActaOperations>();
var schedules = operations.Schedules;

// The schedules register on StartAsync; wait for the durable rows instead of guessing a delay.
using var registrationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
while ((await ListAsync(registrationTimeout.Token)).Count < jobNames.Length)
{
    await Task.Delay(50, registrationTimeout.Token);
}

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
    WHERE namespace = @jobNamespace
    ORDER BY job_name
    """,
    new { jobNamespace }
);
await lab.ShowAsync(
    "Paused, with every cursor in the past",
    """
    SELECT job_name, status, misfire_strategy, next_occurrence_at_utc
    FROM schedules_view
    WHERE namespace = @jobNamespace
      AND job_name IN ('skip-report', 'catch-up-once-report', 'catch-up-all-report')
    ORDER BY job_name
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
    "Each policy's first decision after the resume: Skip's discarded range, CatchUpOnce's covering firing, CatchUpAll's first",
    """
    SELECT job_name, event, details_text
    FROM events_view
    WHERE event_id IN (
        SELECT MIN(event_id)
        FROM events_view
        WHERE namespace = @jobNamespace
          AND event_id > @since
          AND event IN ('schedule.admitted', 'schedule.skipped')
        GROUP BY job_name, event
    )
    ORDER BY job_name, event_id
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

namespace Acta.Concepts.ScheduleMisfire
{
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
}
