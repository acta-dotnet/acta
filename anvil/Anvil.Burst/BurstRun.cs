using System.Diagnostics;
using System.Globalization;
using System.Text;
using Acta;

namespace Anvil.Burst;

/// <summary>What one <c>sys.alerts</c> invocation did, measured from outside the engine.</summary>
/// <param name="Ordinal">1-based position in the run.</param>
/// <param name="Elapsed">Wall clock from making the slot due to the slot coming back to rest.</param>
/// <param name="CursorBefore">The projection cursor before the invocation.</param>
/// <param name="CursorAfter">The projection cursor after it.</param>
/// <param name="EventsProjected">Alertable events inside that cursor range.</param>
/// <param name="DeliveryAttempts">Sends the counting transport was handed during the invocation.</param>
internal sealed record BurstInvocation(
    int Ordinal,
    TimeSpan Elapsed,
    long CursorBefore,
    long CursorAfter,
    int EventsProjected,
    long DeliveryAttempts
)
{
    /// <summary>Generate batches the projection must have taken to cover <see cref="EventsProjected"/>.</summary>
    public int Batches => (EventsProjected + BurstBounds.GenerateBatchSize - 1) / BurstBounds.GenerateBatchSize;
}

/// <summary>
/// Drives one burst certification end to end and prints its verdict: seed a backlog, park the schedules so
/// the harness owns every fire, drain it invocation by invocation, then run the four sweeps the pass
/// condition names (self-heal, resolved-not-delivered, retention eligibility, dashboard pagination).
/// </summary>
/// <remarks>
/// Every invocation goes through the production path: the recurring <c>sys.alerts</c> slot is made due and
/// a real worker claims, dispatches, and completes it. Nothing here constructs the projector or calls into
/// it, which is the whole point - a harness that invoked <c>AlertsJob.Handle</c> directly would certify a
/// method rather than the job.
/// </remarks>
internal sealed class BurstRun(BurstHost host, BurstDb db, BurstOptions options)
{
    // Big enough that the enqueue path is amortized, small enough that a chunk is not a long transaction.
    private const int SeedChunkSize = 5_000;

    // The dashboard's own page size ceiling, which is what the pagination probe should be timing.
    private const int PageSize = 100;

    // One invocation is bounded by the drain's own 30s budget plus a delivery batch, well inside this.
    private static readonly TimeSpan InvocationTimeout = TimeSpan.FromMinutes(10);

    private readonly BurstVerdict _verdict = new();
    private readonly List<BurstInvocation> _invocations = [];
    private int _namespaceId;

    public async Task<int> ExecuteAsync(CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"  Acta alert burst certification | {options.Provider} | schema {options.Schema} | namespace {options.Namespace}"
        );
        Console.WriteLine(
            $"  {Num(options.Events)} alertable events over {BurstJobNames.All.Length} definitions"
                + $" | drain bound {BurstBounds.GenerateBatchSize} x {BurstBounds.GenerateMaxBatches} = {Num(BurstBounds.OneInvocationCeiling)} events/invocation"
                + $" | delivery cap {BurstBounds.DeliverBatchSize}/invocation"
        );
        Console.WriteLine();

        _namespaceId = await ResolveNamespaceAsync(ct);
        await ParkAsync(BurstBounds.AlertsJobName, ct);
        await ParkAsync(BurstBounds.RetentionJobName, ct);

        await SeedAsync(ct);
        await ProveBacklogUnprojectedAsync(ct);
        await AgeEventsAsync(ct);

        await DrainAsync(ct);
        await SelfHealAsync(ct);
        await MeasurePaginationAsync(ct);
        await ProveRetentionEligibleAsync(ct);

        AssertDeliveryCap();
        return _verdict.Print(options);
    }

    // ---- phase 1: a backlog nothing has projected yet -------------------------------------------------

    private async Task SeedAsync(CancellationToken ct)
    {
        Phase("SEED", $"enqueueing {Num(options.Events)} single-attempt failing jobs");
        var enqueue = Stopwatch.StartNew();
        var chunk = new List<JobEnqueueRequest>(SeedChunkSize);
        for (var i = 0; i < options.Events; i++)
        {
            chunk.Add(Request(i));
            if (chunk.Count == SeedChunkSize)
            {
                await host.Jobs.EnqueueBatchAsync(chunk, ct);
                chunk = new List<JobEnqueueRequest>(SeedChunkSize);
            }
        }
        if (chunk.Count > 0)
        {
            await host.Jobs.EnqueueBatchAsync(chunk, ct);
        }

        Phase("SEED", $"enqueued in {enqueue.Elapsed.TotalSeconds:F1}s; waiting for every seeded job to fail");
        var drain = Stopwatch.StartNew();
        var drained = await WaitAsync(
            async () =>
            {
                var terminal = await SeededTerminalAsync(ct);
                Phase("SEED", $"terminal {Num(terminal)} of {Num(options.Events)}");
                return terminal >= options.Events;
            },
            options.SeedTimeout,
            TimeSpan.FromSeconds(5),
            ct
        );
        if (!drained)
        {
            throw new TimeoutException($"The seeded backlog did not reach terminal within {options.SeedTimeout.TotalMinutes:F0} minutes.");
        }

        _verdict.Note(
            "backlog-seeded",
            $"{Num(options.Events)} failed jobs over {BurstJobNames.All.Length} definitions in {drain.Elapsed.TotalSeconds:F1}s"
        );
    }

    // The claim the whole drain measurement rests on: the projector has seen none of this backlog yet.
    // Without it a parked schedule that quietly failed to park would still produce a green run, because
    // whatever had already been projected would simply not show up as work the drain had to do.
    private async Task ProveBacklogUnprojectedAsync(CancellationToken ct)
    {
        var alerts = await AlertTotalAsync(new ListAlertsQuery(JobNamespace: options.Namespace, PageSize: 1, IncludeTotal: true), ct);
        var cursor = await CursorAsync(ct);
        _verdict.Assert(
            "backlog-unprojected",
            alerts == 0 && cursor == 0,
            $"alerts={Num(alerts)} cursor={cursor} before the first invocation (both schedules parked through seeding)"
        );
    }

    private async Task AgeEventsAsync(CancellationToken ct)
    {
        var aged = await db.AgeEventsAsync(_namespaceId, BurstBounds.HorizonBackdate, ct);
        Phase(
            "HORIZON",
            $"aged {Num(aged)} events back {BurstBounds.HorizonBackdate.TotalMinutes:F0} min, past the projection read's safe horizon"
        );
    }

    // ---- phase 2: the drain ---------------------------------------------------------------------------

    private async Task DrainAsync(CancellationToken ct)
    {
        Phase("DRAIN", "invoking sys.alerts until an invocation projects nothing");
        using var footprint = BurstFootprint.Start();
        var wall = Stopwatch.StartNew();
        var drainedAt = TimeSpan.Zero;
        var deadline = DateTime.UtcNow + options.DrainTimeout;
        var productive = new List<BurstInvocation>();

        while (DateTime.UtcNow < deadline)
        {
            var invocation = await InvokeProjectorAsync(ct);
            if (invocation.EventsProjected == 0)
            {
                break;
            }
            productive.Add(invocation);
            drainedAt = wall.Elapsed;
        }

        var projected = productive.Sum(i => i.EventsProjected);
        var alerts = await AlertTotalAsync(new ListAlertsQuery(JobNamespace: options.Namespace, PageSize: 1, IncludeTotal: true), ct);

        _verdict.Assert(
            "backlog-projected",
            projected == options.Events,
            $"projected={Num(projected)} of {Num(options.Events)} seeded events in {productive.Count} invocation(s)"
        );
        // One terminal failure per job and one open incident per (definition, job, kind, reason), so the
        // alert row count is the backlog size. A mismatch means identity collapsed or an event was lost.
        _verdict.Assert(
            "incidents-materialized",
            alerts == options.Events,
            $"alert rows={Num(alerts)} expected={Num(options.Events)} (one open incident per failed job)"
        );

        if (options.OneInvocationExpected)
        {
            _verdict.Assert(
                "projected-one-invocation",
                productive.Count == 1,
                $"invocations that projected work={productive.Count} (a {Num(options.Events)} backlog fits the {Num(BurstBounds.OneInvocationCeiling)}-event bound)"
            );
            _verdict.Assert(
                "drain-wall-clock",
                drainedAt <= BurstBounds.DrainBudget,
                $"{drainedAt.TotalSeconds:F1}s (budget {BurstBounds.DrainBudget.TotalSeconds:F0}s, includes the harness poll between invocations)"
            );
        }
        else
        {
            _verdict.NotApplicable(
                "projected-one-invocation",
                $"a {Num(options.Events)} backlog exceeds the {Num(BurstBounds.OneInvocationCeiling)}-event invocation bound by design"
            );
            _verdict.NotApplicable(
                "drain-wall-clock",
                $"stated for the 10K backlog only; this run drained in {drainedAt.TotalSeconds:F1}s"
            );
        }

        // Forward progress: every invocation but the last one that had work took a full batch, and the
        // last took at least one event. That is the 100K claim - a backlog clears batch after batch under
        // the bound rather than stalling behind it.
        var stalled = productive
            .Where(
                (invocation, index) =>
                    index < productive.Count - 1
                        ? invocation.EventsProjected < BurstBounds.GenerateBatchSize
                        : invocation.EventsProjected < 1
            )
            .ToList();
        _verdict.Assert(
            "forward-progress",
            productive.Count > 0 && stalled.Count == 0,
            productive.Count == 0 ? "no invocation projected anything"
                : stalled.Count == 0
                    ? $"every invocation projected a full batch (min {Num(productive.Min(i => i.EventsProjected))}, max {Num(productive.Max(i => i.EventsProjected))} events)"
                : $"{stalled.Count} invocation(s) projected less than one batch: {string.Join(", ", stalled.Select(i => $"#{i.Ordinal}={i.EventsProjected}"))}"
        );
        _verdict.Note(
            "batches-per-invocation",
            productive.Count == 0
                ? "(nothing projected)"
                : $"min {productive.Min(i => i.Batches)}, max {productive.Max(i => i.Batches)} of the {BurstBounds.GenerateMaxBatches} allowed"
        );
        _verdict.Note(
            "peak-working-set",
            $"{footprint.PeakWorkingSetMb:F0} MB peak over the drain, {footprint.AllocatedMb:F0} MB allocated, {footprint.PeakThreads} peak threads"
        );
    }

    // ---- phase 3: the self-healed sweep ---------------------------------------------------------------

    private async Task SelfHealAsync(CancellationToken ct)
    {
        // The subset is chosen from alerts that were being re-sent moments ago, because the check that
        // follows is "a resolved alert stops being delivered" and a row nothing was delivering proves
        // nothing about that. So: let the reminders come due, take one invocation, and keep the refs that
        // invocation actually re-sent. Every candidate then carries its own non-vacuity evidence.
        var beforeProbe = host.Transport.SentRefs().ToDictionary(r => r, host.Transport.LastSentSequence);
        Phase("HEAL", $"letting {beforeProbe.Count} delivered incident(s) come due, then watching one invocation re-send them");
        await WaitForRemindersAsync(ct);
        await InvokeProjectorAsync(ct);

        var candidates = beforeProbe.Keys.Where(r => host.Transport.LastSentSequence(r) > beforeProbe[r]).Take(options.Healed).ToList();
        var resentBefore = candidates.Count;
        if (candidates.Count == 0)
        {
            _verdict.Assert(
                "self-healed-zero-open",
                false,
                "no open incident was being re-delivered, so no self-heal subject could be chosen"
            );
            _verdict.NotApplicable("resolved-not-delivered", "no re-delivered alert to resolve");
            return;
        }

        var subjects = new List<long>(candidates.Count);
        foreach (var alertRef in candidates)
        {
            if (await host.Operations.Alerts.GetAsync(new AlertRef(alertRef), ct) is { JobId: { } jobId })
            {
                subjects.Add(jobId);
            }
        }

        Phase("HEAL", $"amending {subjects.Count} job input(s) to succeed and restarting them");
        foreach (var jobId in subjects)
        {
            var lookup = JobLookup.ById(jobId);
            await host.Jobs.UpdateJobInputAsync(
                lookup,
                BurstPayloads.Json(new BurstInput($"burst-heal-{jobId.ToString(CultureInfo.InvariantCulture)}", Heal: true)),
                "burst certification self-heal",
                ct: ct
            );
            await host.Jobs.RestartAsync(lookup, "burst certification self-heal", ct: ct);
        }

        var healed = await WaitAsync(
            async () => await SeededSucceededAsync(ct) >= subjects.Count,
            options.SeedTimeout,
            TimeSpan.FromSeconds(2),
            ct
        );
        if (!healed)
        {
            _verdict.Assert("self-healed-zero-open", false, $"the {subjects.Count} restarted job(s) did not all succeed in time");
            _verdict.NotApplicable("resolved-not-delivered", "the self-heal did not complete");
            return;
        }

        // The success events are seconds old, so the projection read withholds them until they fall behind
        // the horizon. Aging them is the same input a pass a minute later would see, without the minute.
        await AgeEventsAsync(ct);

        Phase("HEAL", "projecting the success events, then watching two more invocations deliver");
        while (true)
        {
            var invocation = await InvokeProjectorAsync(ct);
            if (invocation.EventsProjected == 0)
            {
                break;
            }
        }

        // One wait, then two invocations back to back. The wait makes every open incident due again; the
        // pair then sweeps 512 rows in ascending id order, which is more than the namespace has open, so
        // the sweep passes over the id range the resolved rows sit in. Waiting again between the two would
        // instead re-offer the lowest 256 and never reach the rest.
        await WaitForRemindersAsync(ct);
        var mark = host.Transport.Attempts;
        var afterAttempts = 0L;
        for (var i = 0; i < 2; i++)
        {
            afterAttempts += (await InvokeProjectorAsync(ct)).DeliveryAttempts;
        }

        var stillOpen = 0;
        foreach (var jobId in subjects)
        {
            var page = await host.Operations.Alerts.ListAsync(
                new ListAlertsQuery(JobNamespace: options.Namespace, JobId: jobId, UnresolvedOnly: true, PageSize: 1, IncludeTotal: true),
                ct
            );
            stillOpen += (int)(page.TotalCount ?? 0);
        }

        _verdict.Assert("self-healed-zero-open", stillOpen == 0, $"healed jobs={subjects.Count} unresolved alerts left={stillOpen}");

        var deliveredAfterResolve = candidates.Count(r => host.Transport.LastSentSequence(r) > mark);
        _verdict.Assert(
            "resolved-not-delivered",
            deliveredAfterResolve == 0 && resentBefore > 0 && afterAttempts > 0,
            $"{resentBefore} incident(s) were being re-sent before the heal; after it {deliveredAfterResolve} were sent"
                + $" across two invocations that made {afterAttempts} attempts"
        );
    }

    // Delivery re-offers an open incident only once its retry_after instant has passed, and that instant is
    // stamped from the database's clock. Sleeping the interval plus a margin is what turns "settled a
    // moment ago" into "due now" without the harness having to agree with the database about the time.
    private static Task WaitForRemindersAsync(CancellationToken ct) =>
        Task.Delay(BurstBounds.ReminderInterval + TimeSpan.FromMilliseconds(750), ct);

    // ---- phase 4: dashboard pagination ---------------------------------------------------------------

    private async Task MeasurePaginationAsync(CancellationToken ct)
    {
        Phase("PAGES", $"timing the alerts list the dashboard reads, {PageSize} rows a page, up to page {options.PageDepth}");

        var counted = Stopwatch.StartNew();
        var first = await host.Operations.Alerts.ListAsync(
            new ListAlertsQuery(JobNamespace: options.Namespace, PageSize: PageSize, IncludeTotal: true),
            ct
        );
        counted.Stop();

        var timings = new List<(int Page, double Ms)> { (1, counted.Elapsed.TotalMilliseconds) };
        var cursor = first.NextCursor;
        for (var page = 2; page <= options.PageDepth && cursor is not null; page++)
        {
            var timer = Stopwatch.StartNew();
            var next = await host.Operations.Alerts.ListAsync(
                new ListAlertsQuery(JobNamespace: options.Namespace, PageSize: PageSize, Cursor: cursor),
                ct
            );
            timer.Stop();
            timings.Add((page, timer.Elapsed.TotalMilliseconds));
            cursor = next.NextCursor;
        }

        var slowest = timings.MaxBy(t => t.Ms);
        var sampled = timings.Where(t => t.Page is 1 or 2 or 5 or 10 or 25 or 50).Select(t => $"p{t.Page}={t.Ms:F0}ms");
        _verdict.Note(
            "alerts-page-latency",
            $"{string.Join(" ", sampled)} over {timings.Count} page(s) of {PageSize}; slowest p{slowest.Page}={slowest.Ms:F0}ms"
                + $" (page 1 includes the filter-wide count of {Num(first.TotalCount ?? 0)})"
        );
        // No threshold is stated in the plan, so this is reported rather than asserted - but a page that
        // takes longer than a second is the thing an operator would call unresponsive, and it should not
        // pass unremarked.
        _verdict.Assert("alerts-page-under-1s", slowest.Ms <= 1000, $"slowest page {slowest.Ms:F0}ms at page {slowest.Page}");
    }

    // ---- phase 5: retention eligibility ---------------------------------------------------------------

    private async Task ProveRetentionEligibleAsync(CancellationToken ct)
    {
        // Stuck means what the plan means by it: an incident that is still open, because the job it is
        // about never recovered. Those are the rows an operator worries will accumulate forever, so those
        // are the rows the retention cap has to be able to reach - whether or not their delivery settled,
        // which is exactly what the cap is documented to ignore. Filtering to undelivered rows as well
        // looked tighter and was wrong: a small run delivers every alert it raises and the subset came
        // back empty.
        var stuck = new List<AlertListItem>();
        string? cursor = null;
        do
        {
            var page = await host.Operations.Alerts.ListAsync(
                new ListAlertsQuery(JobNamespace: options.Namespace, UnresolvedOnly: true, PageSize: PageSize, Cursor: cursor),
                ct
            );
            stuck.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null && stuck.Count < options.Stuck);

        var subjects = stuck.Take(options.Stuck).ToList();
        if (subjects.Count == 0)
        {
            _verdict.Assert("retention-eligible", false, "no unresolved alert was left to age past the window");
            return;
        }

        var undelivered = subjects.Count(a => a.DeliveryStatus != AlertDeliveryStatusCode.Delivered);
        var aged = await db.AgeAlertsAsync([.. subjects.Select(a => a.AlertId)], BurstBounds.RetentionBackdate, ct);
        Phase(
            "RETAIN",
            $"aged {aged} stuck alert(s) back {BurstBounds.RetentionBackdate.TotalDays:F0} days, past the {BurstBounds.AlertRetention.TotalDays:F0}-day cap"
        );

        await InvokeSlotAsync(BurstBounds.RetentionJobName, ct);

        var surviving = 0;
        foreach (var alert in subjects)
        {
            if (await host.Operations.Alerts.GetAsync(alert.AlertRef, ct) is not null)
            {
                surviving++;
            }
        }

        _verdict.Assert(
            "retention-eligible",
            surviving == 0,
            $"aged {aged} open incident(s) past the cap ({undelivered} of them never delivered);"
                + $" {aged - surviving} purged by one sys.retention pass, {surviving} left"
        );
    }

    // ---- driving one invocation ----------------------------------------------------------------------

    private async Task<BurstInvocation> InvokeProjectorAsync(CancellationToken ct)
    {
        var cursorBefore = await CursorAsync(ct);
        var attemptsBefore = host.Transport.Attempts;
        var elapsed = await InvokeSlotAsync(BurstBounds.AlertsJobName, ct);
        var cursorAfter = await CursorAsync(ct);
        var projected = await db.CountAlertableEventsAsync(_namespaceId, BurstJobNames.All, cursorBefore, cursorAfter, ct);
        var invocation = new BurstInvocation(
            _invocations.Count + 1,
            elapsed,
            cursorBefore,
            cursorAfter,
            projected,
            host.Transport.Attempts - attemptsBefore
        );
        _invocations.Add(invocation);
        Phase(
            "DRAIN",
            $"#{invocation.Ordinal} projected {Num(projected)} events ({invocation.Batches} batch(es)) in {elapsed.TotalSeconds:F1}s,"
                + $" {invocation.DeliveryAttempts} delivery attempt(s), cursor {cursorBefore} -> {cursorAfter}"
        );
        return invocation;
    }

    // Over every invocation the run made, not just the drain's: the cap is a per-invocation property, so
    // the honest reading is the worst one anywhere in the run, and the line names which one that was.
    private void AssertDeliveryCap()
    {
        if (_invocations.Count == 0)
        {
            _verdict.Assert("delivery-cap", false, "no invocation ran, so the cap was never exercised");
            return;
        }

        var worst = _invocations.MaxBy(i => i.DeliveryAttempts)!;
        _verdict.Assert(
            "delivery-cap",
            worst.DeliveryAttempts <= BurstBounds.DeliverBatchSize,
            $"max {worst.DeliveryAttempts} external attempts in one invocation (#{worst.Ordinal}) of {_invocations.Count},"
                + $" cap {BurstBounds.DeliverBatchSize}"
        );
    }

    /// <summary>
    /// Makes one recurring slot due right now and waits for the worker to finish the execution it starts.
    /// </summary>
    /// <remarks>
    /// Both system schedules stay paused for the whole run, so the slot's next-run instant is null and
    /// nothing but this call can make it claimable - which is what makes "one invocation" a unit the
    /// harness owns rather than a window it hopes the cron did not fire twice inside. The claim itself,
    /// the dispatch, the handler and the completion are all the production path.
    /// </remarks>
    private async Task<TimeSpan> InvokeSlotAsync(string jobName, CancellationToken ct)
    {
        var lookup = JobLookup.ByDeduplicationKey(options.Namespace, jobName);
        var before = await SlotAsync(jobName, ct);
        var due = await host.Jobs.RescheduleAsync(lookup, DateTime.UtcNow, "burst certification invocation", ct: ct);
        if (due.Action != ControlAction.Applied)
        {
            throw new InvalidOperationException(
                $"Could not make the '{jobName}' slot due: {due.Action} (status {due.Status}). The run cannot own its invocations."
            );
        }

        var started = Stopwatch.StartNew();
        var finished = await WaitAsync(
            async () =>
            {
                var slot = await SlotAsync(jobName, ct);
                // The attempt counter moved AND the row is at rest. Which resting status it lands in is
                // the schedule set's business, not this harness's - Ready under the timed pause ParkAsync
                // takes, Paused if a slot ever computes an empty next-run - so the condition names the two
                // in-flight statuses instead of guessing the resting one.
                return slot.ExecutionNumber > before.ExecutionNumber
                    && slot.Status is not (JobStatusCode.Dispatched or JobStatusCode.Executing);
            },
            InvocationTimeout,
            TimeSpan.FromMilliseconds(200),
            ct
        );
        return finished
            ? started.Elapsed
            : throw new TimeoutException($"The '{jobName}' invocation did not finish within {InvocationTimeout.TotalMinutes:F0} minutes.");
    }

    // ---- reads ---------------------------------------------------------------------------------------

    /// <summary>
    /// The projector's cursor, read the way the read surface exposes it: the <c>sys.alerts</c> slot's own
    /// durable variable. Zero before the first checkpoint.
    /// </summary>
    private async Task<long> CursorAsync(CancellationToken ct)
    {
        var checkpoints = await host.Jobs.GetCheckpointsAsync(
            JobLookup.ByDeduplicationKey(options.Namespace, BurstBounds.AlertsJobName),
            ct
        );
        foreach (var slot in checkpoints)
        {
            if (string.Equals(slot.Name, BurstBounds.CursorVariableName, StringComparison.Ordinal) && slot.Value is { IsNone: false } value)
            {
                return long.Parse(Encoding.UTF8.GetString(value.Data.Span), CultureInfo.InvariantCulture);
            }
        }
        return 0;
    }

    private async Task<JobListItem> SlotAsync(string jobName, CancellationToken ct)
    {
        var page = await host.Operations.Ledger.ListJobsAsync(
            new ListJobsQuery(JobNamespace: options.Namespace, JobName: jobName, PageSize: 1),
            ct
        );
        return page.Items.Count > 0
            ? page.Items[0]
            : throw new InvalidOperationException($"The '{jobName}' recurring slot has no row in namespace '{options.Namespace}'.");
    }

    private async Task<long> AlertTotalAsync(ListAlertsQuery query, CancellationToken ct) =>
        (await host.Operations.Alerts.ListAsync(query, ct)).TotalCount ?? 0;

    private async Task<long> SeededTerminalAsync(CancellationToken ct) =>
        (
            await host.Operations.Ledger.ListJobsAsync(
                new ListJobsQuery(
                    JobNamespace: options.Namespace,
                    CorrelationKey: options.RunId,
                    PageSize: 1,
                    IncludeTotal: true,
                    TerminalOnly: true
                ),
                ct
            )
        ).TotalCount
        ?? 0;

    private async Task<long> SeededSucceededAsync(CancellationToken ct) =>
        (
            await host.Operations.Ledger.ListJobsAsync(
                new ListJobsQuery(
                    JobNamespace: options.Namespace,
                    CorrelationKey: options.RunId,
                    Status: JobStatusCode.Succeeded,
                    PageSize: 1,
                    IncludeTotal: true
                ),
                ct
            )
        ).TotalCount
        ?? 0;

    // ---- setup ---------------------------------------------------------------------------------------

    private async Task<int> ResolveNamespaceAsync(CancellationToken ct)
    {
        // The worker upserts its namespaces row during startup; the harness can reach here first.
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try
            {
                return await db.NamespaceIdAsync(options.Namespace, ct);
            }
            catch (InvalidOperationException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct);
            }
        }
    }

    private async Task ParkAsync(string jobName, CancellationToken ct)
    {
        var lookup = new ScheduleLookup(JobLookup.ByDeduplicationKey(options.Namespace, jobName), BurstBounds.DefaultScheduleName);
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            // A timed pause rather than an indefinite one, for a reason the first smoke run found. An
            // indefinite pause drops out of the slot's next-run MIN entirely, and a recurring completion
            // that computes a null MIN parks the slot Paused with JobSchedulesExhausted - a legitimate
            // state, but one the harness would then have to re-arm from on every single invocation. A
            // pause with a far-future wake keeps the MIN non-null, so the slot rests Ready and
            // unclaimable between the invocations this harness makes.
            var result = await host.Operations.Schedules.PauseAsync(
                lookup,
                untilUtc: DateTime.UtcNow.AddYears(1),
                reasonMessage: "burst certification drives this slot by hand",
                ct: ct
            );
            if (result.Action == ControlAction.Applied)
            {
                Phase("PARK", $"{jobName} schedule paused; every fire from here is the harness making the slot due");
                return;
            }
            await Task.Delay(500, ct);
        }

        throw new InvalidOperationException(
            $"Could not pause the '{jobName}' schedule within two minutes; the run cannot own its invocations."
        );
    }

    private JobEnqueueRequest Request(int index) =>
        new(
            options.Namespace,
            BurstJobNames.All[index % BurstJobNames.All.Length],
            BurstPayloads.Json(new BurstInput($"burst-{index.ToString(CultureInfo.InvariantCulture)}", Heal: false)),
            DeduplicationKey: $"burst/{options.RunId}/{index.ToString(CultureInfo.InvariantCulture)}",
            CorrelationKey: options.RunId,
            Tags: [new TagInput("lab", "anvil-burst"), new TagInput("run", options.RunId)]
        );

    // ---- plumbing ------------------------------------------------------------------------------------

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan timeout, TimeSpan poll, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }
            await Task.Delay(poll, ct);
        }
        return false;
    }

    private static void Phase(string phase, string detail) => Console.WriteLine($"  {DateTime.Now:HH:mm:ss}  {phase, -8} {detail}");

    private static string Num<T>(T value)
        where T : IFormattable => value.ToString("N0", CultureInfo.InvariantCulture);
}
