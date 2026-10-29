using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The scheduling-admission loop of one worker namespace: reads the schedules due for evaluation, plans each slot's
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

        var channel = WorkerWakeupChannel.WorkerNamespace(namespaceName);
        while (!ct.IsCancellationRequested)
        {
            TimeSpan sleep;
            try
            {
                var pass = await AdmitOnceAsync(namespaceName, ct);
                if (pass.Admitted > 0 || pass.Stale > 0)
                {
                    continue;
                }

                // The claim loop's sleep rule: until the horizon, floored and jittered, never past the safety poll.
                var now = await clock.GetUtcNowAsync(ct);
                sleep = WorkerClaimer.ComputeSleep(
                    new ClaimHorizon(now, pass.NextWakeUtc),
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
    /// One pass over the namespace: plan every slot with a due schedule, apply, replan the groups another writer
    /// moved, and say what was admitted and when the engine next has something to do.
    /// </summary>
    public async Task<AdmissionPass> AdmitOnceAsync(string namespaceName, CancellationToken ct)
    {
        var namespaceId = context.NamespaceIds[namespaceName];
        var admitted = 0;
        var stale = 0;
        for (var replan = 0; replan <= MaxReplans; replan++)
        {
            var now = await clock.GetUtcNowAsync(ct);
            var due = (await schedules.GetDueSchedulesAsync(namespaceId, now, SchedulesPerPass, ct)).Schedules;
            var plan = Plan(namespaceId, due, now);
            if (plan.Groups.Count == 0)
            {
                break;
            }

            var outcomes = (await schedules.AdmitFiringsAsync(plan, ct)).Slots;
            var appliedAdmissions = outcomes.Count(o => o.Applied && plan.Groups.Any(g => g.JobId == o.JobId && g.Admit));
            admitted += appliedAdmissions;
            stale = outcomes.Count(o => !o.Applied);
            if (appliedAdmissions > 0)
            {
                await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(namespaceName), WorkerWakeupReason.WorkAvailable, ct);
            }

            if (stale == 0)
            {
                break;
            }
        }

        return new AdmissionPass(admitted, stale, await schedules.GetScheduleHorizonAsync(namespaceId, ct));
    }

    /// <summary>
    /// The plan: one group per slot. A slot admits at most one firing per pass, every member whose evaluation
    /// selected an occurrence contributing to it; a member nothing changed for writes no row.
    /// </summary>
    internal static AdmitFiringsCommand Plan(int namespaceId, IReadOnlyList<GetDueSchedulesRow> due, DateTime nowUtc)
    {
        var groups = new List<AdmitFiringsGroup>();
        var rows = new List<AdmitFiringsValues>();
        foreach (var slot in due.GroupBy(d => d.JobId))
        {
            var first = slot.First();
            var capacity = Capacity(first.SlotStatus, first.FiringStatus);
            var contributors = new List<FiringContributor>();
            var slotRows = new List<AdmitFiringsValues>();
            var firingId = Guid.CreateVersion7();
            foreach (var s in slot)
            {
                var timing = new ScheduleTiming(s.Expression, s.ExpressionKind, s.TimeZoneId, s.IntervalAnchorAtUtc);
                var cursor = new ScheduleCursor(s.NextOccurrenceAtUtc, s.DecidedThroughAtUtc, s.Status, s.PausedUntilUtc);
                var e = ScheduleEvaluator.Evaluate(
                    timing,
                    s.MisfireStrategy,
                    TimeSpan.FromSeconds(s.MisfireGraceSeconds),
                    cursor,
                    capacity,
                    nowUtc
                );
                if (!e.Changes(cursor, s.EvaluationStatus, s.NextEvaluationAtUtc))
                {
                    continue;
                }

                string? admittedDetails = null;
                if (e.Admit is { } admit)
                {
                    contributors.Add(
                        new FiringContributor(
                            s.ScheduleId,
                            s.ScheduleName,
                            admit.OccurrenceAtUtc,
                            s.TimeZoneId ?? "UTC",
                            admit.CoveredThroughUtc,
                            s.TimingFingerprint
                        )
                    );
                    admittedDetails = Details(
                        ("schedule", s.ScheduleName),
                        ("occurrence", Iso(admit.OccurrenceAtUtc)),
                        ("coveredThrough", Iso(admit.CoveredThroughUtc)),
                        ("firingId", firingId.ToString("N"))
                    );
                }

                var skippedDetails = e.SkippedFromUtc is { } from
                    ? Details(("schedule", s.ScheduleName), ("from", Iso(from)), ("through", Iso(e.SkippedThroughUtc!.Value)))
                    : null;
                slotRows.Add(
                    new AdmitFiringsValues(
                        s.ScheduleId,
                        s.JobId,
                        s.ScheduleName,
                        s.StateVersion,
                        e.NextOccurrenceAtUtc,
                        e.DecidedThroughAtUtc,
                        e.NextEvaluationAtUtc,
                        e.Status,
                        e.PauseEnded,
                        admittedDetails,
                        skippedDetails
                    )
                );
            }

            if (slotRows.Count == 0)
            {
                continue;
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
            groups.Add(new AdmitFiringsGroup(first.JobId, first.SlotVersion, contributors.Count > 0, envelope));
            rows.AddRange(slotRows);
        }

        return new AdmitFiringsCommand(namespaceId, groups, rows);
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

    private static string Iso(DateTime value) => DbParams.ToUtc(value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>ASCII JSON for a decision event: names are dotted kebab, the rest are instants and ids, so nothing needs escaping.</summary>
    private static string Details(params ReadOnlySpan<(string Name, string Value)> facts)
    {
        var text = new System.Text.StringBuilder("{");
        for (var i = 0; i < facts.Length; i++)
        {
            text.Append(i == 0 ? "\"" : ",\"").Append(facts[i].Name).Append("\":\"").Append(facts[i].Value).Append('"');
        }

        return text.Append('}').ToString();
    }
}

/// <summary>What one admission pass did, and when the namespace next needs the engine.</summary>
internal sealed record AdmissionPass(int Admitted, int Stale, DateTime? NextWakeUtc);
