using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The scheduling-admission loop of one worker namespace: reads the schedules due for evaluation, plans each group's
/// decision with <see cref="ScheduleEvaluator"/> outside any transaction, applies the plan atomically, and wakes the
/// claim loop when it admitted a firing. Leaderless: every host runs one, a plan another host beat to the row is
/// stale and replans, and the claim loop never waits on it. <see cref="AdmitOnceAsync"/> is one pass, the test seam.
/// </summary>
internal sealed class ScheduleAdmitter(
    SchedulesOperations schedules,
    IActaClock clock,
    WorkerContext context,
    IWorkerWakeup wakeup,
    WorkerWakeupPublisher publisher,
    JobsOptions options,
    CalendarsOperations calendars,
    ILogger log,
    ActaMetrics? metrics
)
{
    /// <summary>Due schedules one pass reads; a dense namespace is served in rotation, never monopolized.</summary>
    internal const int SchedulesPerPass = 100;

    /// <summary>Replans of one pass before it yields to the next; a lost race is cheap, a loop of them is a sign to back off.</summary>
    private const int MaxReplans = 3;

    public async Task RunAsync(string namespaceName, CancellationToken ct)
    {
        if (context.WorkerRegistration is null)
        {
            return;
        }

        // Its own signal, apart from the claim loop's: a shared auto-reset one would take wakes from the claim loop.
        var channel = WorkerWakeupChannel.Admission(namespaceName);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                // The horizon read is the idle loop's one statement; a pass runs only when something is due.
                var horizon = await schedules.GetScheduleHorizonAsync(context.NamespaceIds[namespaceName], ct);
                var (now, next) = (horizon.DbNowUtc, horizon.NextEvaluationAtUtc);
                if (next <= now)
                {
                    var pass = await AdmitOnceAsync(namespaceName, ct);
                    if (pass.Admitted > 0 || pass.Stale > 0)
                    {
                        continue;
                    }
                    (now, next) = (pass.DbNowUtc, pass.NextWakeUtc);
                }

                // The claim loop's sleep rule: until the horizon, floored and jittered, never past the safety poll.
                sleep = WorkerClaimer.ComputeSleep(
                    new ClaimHorizon(now, next),
                    options.SafetyPollInterval,
                    options.MinPollFloor,
                    options.ClaimIdleJitterMax
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A failed pass backs off a full safety interval: hammering a down database would log errors many times a second.
                ActaLog.AdmissionPassFailed(log, ex, namespaceName);
                sleep = options.SafetyPollInterval;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var result = await wakeup.WaitAsync(channel, sleep, ct);
                metrics?.RecordWakeupWait(namespaceName, WorkerWakeupPublisher.WaitResultTag(result));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass over the namespace: plan every slot and per-occurrence schedule that is due, apply, replan the groups
    /// another writer moved, and say what was admitted and when the engine next has something to do.
    /// </summary>
    public async Task<AdmissionPass> AdmitOnceAsync(string namespaceName, CancellationToken ct)
    {
        var namespaceId = context.NamespaceIds[namespaceName];
        var started = Stopwatch.GetTimestamp();
        var admitted = 0;
        var stale = 0;
        var staleTotal = 0;
        for (var replan = 0; replan <= MaxReplans; replan++)
        {
            var now = await clock.GetUtcNowAsync(ct);
            var due = (await schedules.ListDueSchedulesAsync(namespaceId, now, SchedulesPerPass, ct)).Schedules;
            if (replan == 0 && metrics is not null)
            {
                foreach (var s in due)
                {
                    if (s.NextEvaluationAtUtc is { } at)
                    {
                        metrics.RecordScheduleDueAge(namespaceName, now - at);
                    }
                }
            }

            var resolve = await calendars.PreloadAsync(namespaceId, due.SelectMany(s => s.Timing().CalendarNames()), ct);
            var decisions = metrics is null ? null : new List<PlannedMeasurement>();
            var plan = Plan(namespaceId, due, now, resolve, log, decisions);
            if (plan.Groups.Count == 0)
            {
                break;
            }

            var outcomes = (await schedules.AdmitScheduleFiringsAsync(plan, ct)).Groups;
            var appliedAdmissions = outcomes.Count(o => o.Applied && plan.Groups[o.Ordinal].Admit);
            admitted += appliedAdmissions;
            stale = outcomes.Count(o => !o.Applied);
            staleTotal += stale;
            if (decisions is not null)
            {
                // Only an applied group's decisions count; a stale group's replan records them when it lands.
                var applied = outcomes.Where(o => o.Applied).Select(o => o.Ordinal).ToHashSet();
                foreach (var decision in decisions.Where(d => applied.Contains(d.Ordinal)))
                {
                    metrics!.RecordScheduleDecision(namespaceName, decision.Decision);
                    if (decision.Lag is { } lag)
                    {
                        metrics.RecordScheduleLag(namespaceName, lag);
                    }
                }
            }

            if (appliedAdmissions > 0)
            {
                await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
            }

            if (stale == 0)
            {
                break;
            }
        }

        metrics?.RecordSchedulePass(namespaceName, Stopwatch.GetElapsedTime(started), staleTotal);
        var horizon = await schedules.GetScheduleHorizonAsync(namespaceId, ct);
        return new AdmissionPass(admitted, stale, horizon.NextEvaluationAtUtc, horizon.DbNowUtc);
    }

    /// <summary>
    /// The plan: one group per slot, every member whose evaluation selected an occurrence contributing to its one
    /// firing, and one group per per-occurrence schedule. A slot member nothing changed for rides as a check-only row,
    /// and a group that would carry only those, like a per-occurrence schedule nothing changed for, is left out. A group's
    /// ordinal is its index. Calendars resolve through <paramref name="resolve"/>, preloaded for the rows; a schedule
    /// whose source fails is parked as an evaluation error and, given a logger, said so. Given a list, the decisions each
    /// group's rows took are collected for the meter, by ordinal.
    /// </summary>
    internal static AdmitScheduleFiringsCommand Plan(
        int namespaceId,
        IReadOnlyList<DueSchedule> due,
        DateTime nowUtc,
        Func<ScheduleTiming, ScheduleSource> resolve,
        ILogger? log = null,
        List<PlannedMeasurement>? decisions = null
    )
    {
        var groups = new List<AdmitScheduleFiringsGroup>();
        var rows = new List<AdmitScheduleFiringsValues>();
        foreach (var slot in due.Where(d => d.JobId is not null).GroupBy(d => d.JobId!.Value))
        {
            PlanSlot(slot.Key, [.. slot], nowUtc, resolve, log, groups, rows, decisions);
        }

        foreach (var schedule in due.Where(d => d.JobId is null))
        {
            PlanOccurrence(schedule, nowUtc, resolve, log, groups, rows, decisions);
        }

        return new AdmitScheduleFiringsCommand(namespaceId, groups, rows);
    }

    /// <summary>A slot admits at most one firing per pass, the contributors frozen in its envelope.</summary>
    private static void PlanSlot(
        long jobId,
        IReadOnlyList<DueSchedule> members,
        DateTime nowUtc,
        Func<ScheduleTiming, ScheduleSource> resolve,
        ILogger? log,
        List<AdmitScheduleFiringsGroup> groups,
        List<AdmitScheduleFiringsValues> rows,
        List<PlannedMeasurement>? decisions
    )
    {
        var first = members[0];
        if (first is not { SlotStatus: { } slotStatus, SlotVersion: { } slotVersion })
        {
            return;
        }

        var ordinal = groups.Count;
        var capacity = Capacity(slotStatus, first.FiringStatus);
        var contributors = new List<FiringContributor>();
        var slotRows = new List<AdmitScheduleFiringsValues>();
        var checks = new List<AdmitScheduleFiringsValues>();
        var firingId = Guid.CreateVersion7();
        foreach (var s in members)
        {
            var e = Evaluate(s, capacity, nowUtc, resolve, log);
            if (!e.Changes(Cursor(s), s.EvaluationStatus, s.NextEvaluationAtUtc))
            {
                checks.Add(AdmitScheduleFiringsValues.Check(ordinal, s.ScheduleId, s.ScheduleName, s.StateVersion, s.ScheduleVersion));
                continue;
            }

            string? admittedDetails = null;
            if (e.Admit is { } admit)
            {
                contributors.Add(Contributor(s, admit));
                admittedDetails = AdmittedDetails(s, admit, firingId);
            }

            slotRows.Add(Row(ordinal, s, e, admittedDetails, SkippedDetails(s, e)));
            Decide(decisions, ordinal, e, dropped: false, nowUtc);
        }

        if (slotRows.Count == 0)
        {
            return;
        }

        var envelope =
            contributors.Count == 0
                ? null
                : new FiringEnvelope(
                    firingId,
                    FiringEnvelope.ScheduledKind,
                    nowUtc,
                    contributors.OrderBy(c => c.ScheduleName, StringComparer.Ordinal).ToList()
                ).ToJson();
        groups.Add(new AdmitScheduleFiringsGroup(ordinal, jobId, slotVersion, null, null, contributors.Count > 0, envelope));
        rows.AddRange(slotRows);
        rows.AddRange(checks);
    }

    /// <summary>
    /// A per-occurrence schedule is a group of its own: an admitted occurrence becomes a fresh root, keyed so the same
    /// occurrence is never admitted twice. At <c>MaxOutstanding</c> the schedule waits for a settlement, or under Drop
    /// skips the occurrence with the reason <c>capacity</c> and moves on.
    /// </summary>
    private static void PlanOccurrence(
        DueSchedule s,
        DateTime nowUtc,
        Func<ScheduleTiming, ScheduleSource> resolve,
        ILogger? log,
        List<AdmitScheduleFiringsGroup> groups,
        List<AdmitScheduleFiringsValues> rows,
        List<PlannedMeasurement>? decisions
    )
    {
        var full = s.OutstandingCount >= s.MaxOutstanding;
        var capacity = full && s.FullBehavior != ScheduleFullBehaviorCode.Drop ? AdmissionCapacity.Full : AdmissionCapacity.Available;
        var e = Evaluate(s, capacity, nowUtc, resolve, log);
        if (!e.Changes(Cursor(s), s.EvaluationStatus, s.NextEvaluationAtUtc))
        {
            return;
        }

        var ordinal = groups.Count;
        var group = new AdmitScheduleFiringsGroup(ordinal, null, null, s.ScheduleId, s.StateVersion, false, null);
        string? admittedDetails = null;
        var skippedDetails = SkippedDetails(s, e);
        if (e.Admit is { } admit)
        {
            if (full)
            {
                skippedDetails = RangeDetails(s, e.SkippedFromUtc ?? admit.OccurrenceAtUtc, admit.CoveredThroughUtc, "capacity");
            }
            else
            {
                var firingId = Guid.CreateVersion7();
                admittedDetails = AdmittedDetails(s, admit, firingId);
                group = group with
                {
                    Admit = true,
                    Envelope = new FiringEnvelope(firingId, FiringEnvelope.ScheduledKind, nowUtc, [Contributor(s, admit)]).ToJson(),
                    JobRef = Guid.CreateVersion7(),
                    DeduplicationKey = OccurrenceKey(s.ScheduleId, admit.OccurrenceAtUtc),
                };
            }
        }

        groups.Add(group);
        rows.Add(Row(ordinal, s, e, admittedDetails, skippedDetails));
        Decide(decisions, ordinal, e, dropped: full && e.Admit is not null, nowUtc);
    }

    /// <summary>
    /// The decisions one evaluation took, for the meter; a range skipped or expired before an admitted occurrence
    /// counts too, and an occurrence dropped at capacity counts as skipped rather than admitted.
    /// </summary>
    private static void Decide(List<PlannedMeasurement>? decisions, int ordinal, ScheduleEvaluation e, bool dropped, DateTime nowUtc)
    {
        if (decisions is null)
        {
            return;
        }

        if (e.SkippedFromUtc is not null || dropped)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "skipped", null));
        }

        if (e.ExpiredFromUtc is not null)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "expired", null));
        }

        if (!dropped && e.Admit is { } admit)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "admitted", nowUtc - admit.OccurrenceAtUtc));
        }
        else if (e.Status == ScheduleEvaluationStatusCode.WaitingForCapacity)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "blocked", null));
        }

        if (e.Status == ScheduleEvaluationStatusCode.Exhausted)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "exhausted", null));
        }

        if (e.Error is not null)
        {
            decisions.Add(new PlannedMeasurement(ordinal, "error", null));
        }
    }

    /// <summary>
    /// What the slot can take: Idle with no outstanding firing admits; a queued, running, waiting, or retrying firing
    /// fills it until settlement wakes the members; a held, cancelled, or finished slot needs an operator.
    /// </summary>
    internal static AdmissionCapacity Capacity(JobStatusCode slotStatus, CheckpointStatusCode? firingStatus) =>
        slotStatus switch
        {
            JobStatusCode.Idle when firingStatus != CheckpointStatusCode.Pending => AdmissionCapacity.Available,
            JobStatusCode.Idle or JobStatusCode.Ready or JobStatusCode.Blocked or JobStatusCode.Suspended or JobStatusCode.Executing =>
                AdmissionCapacity.Full,
            _ => AdmissionCapacity.Unavailable,
        };

    /// <summary>The key an automatic occurrence's root is inserted under: one root per schedule and nominal instant.</summary>
    internal static string OccurrenceKey(int scheduleId, DateTime occurrenceUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"sys.occurrence.{scheduleId}.{DbParams.ToUtc(occurrenceUtc).Ticks}");

    private static ScheduleCursor Cursor(DueSchedule s) => new(s.NextOccurrenceAtUtc, s.Status, s.PausedUntilUtc, s.EvaluationStatus);

    private static ScheduleEvaluation Evaluate(
        DueSchedule s,
        AdmissionCapacity capacity,
        DateTime nowUtc,
        Func<ScheduleTiming, ScheduleSource> resolve,
        ILogger? log
    )
    {
        var policy = new SchedulePolicy(
            s.MisfireStrategy,
            TimeSpan.FromSeconds(s.MisfireGraceSeconds),
            s.ExpireAfterSeconds is { } expireAfter ? TimeSpan.FromSeconds(expireAfter) : null,
            s.ExpireAction ?? ScheduleExpireActionCode.Unspecified
        );
        var e = ScheduleEvaluator.Evaluate(resolve(s.Timing()), policy, Cursor(s), capacity, nowUtc);
        if (e.Error is { } error && log is not null && s.EvaluationStatus != ScheduleEvaluationStatusCode.EvaluationError)
        {
            ActaLog.ScheduleEvaluationFailed(log, s.DefinitionName, $"schedule '{s.ScheduleName}': {error}");
        }

        return e;
    }

    private static FiringContributor Contributor(DueSchedule s, AdmittedOccurrence admit) =>
        new(s.ScheduleId, s.ScheduleName, admit.OccurrenceAtUtc, s.TimeZoneId ?? "UTC", admit.CoveredThroughUtc, s.TimingFingerprint);

    private static AdmitScheduleFiringsValues Row(
        int ordinal,
        DueSchedule s,
        ScheduleEvaluation e,
        string? admittedDetails,
        string? skippedDetails
    ) =>
        new(
            ordinal,
            s.ScheduleId,
            s.ScheduleName,
            s.StateVersion,
            s.ScheduleVersion,
            e.NextOccurrenceAtUtc,
            e.NextEvaluationAtUtc,
            e.Status,
            e.PauseEnded,
            admittedDetails,
            skippedDetails,
            ExpiredDetails(s, e),
            e.PausedByExpiry
        );

    private static string AdmittedDetails(DueSchedule s, AdmittedOccurrence admit, Guid firingId) =>
        EventDetails.Text(
            ("schedule", s.ScheduleName),
            ("occurrence", EventDetails.Instant(admit.OccurrenceAtUtc)),
            ("coveredThrough", EventDetails.Instant(admit.CoveredThroughUtc)),
            ("firingId", firingId.ToString("N"))
        )!;

    private static string? SkippedDetails(DueSchedule s, ScheduleEvaluation e) =>
        e.SkippedFromUtc is { } from ? RangeDetails(s, from, e.SkippedThroughUtc!.Value, e.SourceChanged ? "source-changed" : null) : null;

    private static string? ExpiredDetails(DueSchedule s, ScheduleEvaluation e) =>
        e.ExpiredFromUtc is { } from ? RangeDetails(s, from, e.ExpiredThroughUtc!.Value, "expired") : null;

    /// <summary>A decided range as the ledger records it; the reason is left out when the misfire policy alone decided it.</summary>
    private static string RangeDetails(DueSchedule s, DateTime fromUtc, DateTime throughUtc, string? reason) =>
        EventDetails.Text(
            ("schedule", s.ScheduleName),
            ("from", EventDetails.Instant(fromUtc)),
            ("through", EventDetails.Instant(throughUtc)),
            ("reason", reason)
        )!;
}

/// <summary>What one admission pass did, and when the namespace next needs the engine by the database's clock.</summary>
internal sealed record AdmissionPass(int Admitted, int Stale, DateTime? NextWakeUtc, DateTime DbNowUtc);

/// <summary>One decision a plan's group took, for the meter once the group applies; an admitted occurrence carries its lag.</summary>
internal sealed record PlannedMeasurement(int Ordinal, string Decision, TimeSpan? Lag);
