using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Acta;

/// <summary>
/// Owns the <c>Acta</c> meter and its instruments, a process-wide singleton: one execution measurement per durable
/// completion in <see cref="Acta.WorkerFinalizer"/>, one claim measurement per claim attempt in
/// <see cref="Acta.WorkerClaimer"/>, the scheduling decisions and lag of each <see cref="Acta.ScheduleAdmitter"/> pass,
/// and the live in-flight count. Counters are additive, so no running totals are kept in process. Tag keys and values
/// are the operator-facing metric surface, pinned by <c>ActaMetricsTests</c> and kept low-cardinality (namespace,
/// job_name, outcome, reason, result, state, decision); the unbounded job id and execution number stay off metrics.
/// </summary>
internal sealed class ActaMetrics : IDisposable
{
    public const string MeterName = "Acta";

    /// <summary>
    /// Exposed so tests can scope a MeterListener to THIS instance's meter; same-named meters from
    /// parallel test hosts would otherwise cross-capture identical instrument/tag measurements.
    /// </summary>
    internal Meter Meter { get; }

    private readonly Counter<long> _executions;
    private readonly Histogram<double> _executionDurationMs;
    private readonly Counter<long> _claims;
    private readonly Counter<long> _steps;
    private readonly Counter<long> _lockReleaseFailures;
    private readonly Counter<long> _alertProjectionSkips;
    private readonly Counter<long> _wakeupPublishes;
    private readonly Counter<long> _wakeupPublishFailures;
    private readonly Counter<long> _wakeupWaits;
    private readonly UpDownCounter<long> _unsettledCompletions;
    private readonly Histogram<int> _completionBatches;
    private readonly Counter<long> _recoveryPasses;
    private readonly Counter<long> _fencedAttempts;
    private readonly Histogram<double> _leaseHeadroomMs;
    private readonly Histogram<double> _alertLagSeconds;
    private readonly Counter<long> _scheduleDecisions;
    private readonly Histogram<double> _scheduleLagMs;
    private readonly Histogram<double> _scheduleDueAgeSeconds;
    private readonly Histogram<double> _schedulePassMs;
    private readonly Counter<long> _scheduleStaleGroups;
    private readonly Histogram<double> _firingStartDelayMs;

    /// <summary>
    /// Each worker registers its namespace's live in-flight count by state; the observable gauge reads them
    /// on collection. Reading live state beats a running total that can drift on a crash.
    /// </summary>
    private readonly List<(string Namespace, Func<InFlightJobState, int> LiveCount)> _inFlightSources = [];

    // The state tag's values, kept off ToString() so an enum rename cannot rename an operator-facing dimension.
    private static readonly (InFlightJobState State, string Tag)[] InFlightStates =
    [
        (InFlightJobState.Claimed, "claimed"),
        (InFlightJobState.Executing, "executing"),
        (InFlightJobState.Finalizing, "finalizing"),
    ];

    public ActaMetrics()
    {
        Meter = new Meter(MeterName);
        _executions = Meter.CreateCounter<long>("acta.executions", "{execution}", "Completed job executions, tagged by outcome.");
        _executionDurationMs = Meter.CreateHistogram<double>("acta.duration", "ms", "Handler execution duration.");
        _claims = Meter.CreateCounter<long>("acta.claims", "{claim}", "Job claim attempts, tagged by result.");
        _steps = Meter.CreateCounter<long>("acta.steps", "{step}", "Durable step outcomes, tagged by outcome.");
        _lockReleaseFailures = Meter.CreateCounter<long>(
            "acta.lock.release.failures",
            "{failure}",
            "Best-effort lock releases that failed and were left for TTL cleanup."
        );
        _alertProjectionSkips = Meter.CreateCounter<long>(
            "acta.alert.projection.skips",
            "{skip}",
            "Poison alert events durably skipped by the automatic projector."
        );
        _wakeupPublishes = Meter.CreateCounter<long>(
            "acta.wakeup.publish.attempts",
            "{publish}",
            "Wake publishes, tagged by channel and reason."
        );
        _wakeupPublishFailures = Meter.CreateCounter<long>(
            "acta.wakeup.publish.failures",
            "{publish}",
            "Wake publishes the transport failed to deliver."
        );
        _wakeupWaits = Meter.CreateCounter<long>("acta.wakeup.waits", "{wait}", "Idle claim-loop waits, tagged by how they returned.");
        _unsettledCompletions = Meter.CreateUpDownCounter<long>(
            "acta.completions.unsettled",
            "{completion}",
            "Completion writes still repeating after a failure."
        );
        _completionBatches = Meter.CreateHistogram<int>(
            "acta.completions.batch",
            "{completion}",
            "Completions the finalizer wrote in one call."
        );
        _recoveryPasses = Meter.CreateCounter<long>(
            "acta.recovery.passes",
            "{pass}",
            "Recovery passes this process ran, tagged by outcome: completed or failed."
        );
        _fencedAttempts = Meter.CreateCounter<long>(
            "acta.executions.fenced",
            "{execution}",
            "Attempts stopped without a completion because a write found their job held by another execution."
        );
        _leaseHeadroomMs = Meter.CreateHistogram<double>(
            "acta.lease.headroom",
            "ms",
            "Time an attempt had left before its lease runway ran out when a renewal confirmed its lease."
        );
        _alertLagSeconds = Meter.CreateHistogram<double>(
            "acta.alerts.lag",
            "s",
            "How far the alert projector's cursor stood behind the database clock when a pass began."
        );
        _scheduleDecisions = Meter.CreateCounter<long>(
            "acta.schedule.decisions",
            "{decision}",
            "Scheduling decisions an admission pass applied, tagged by decision: admitted, skipped, expired, blocked, exhausted, or error."
        );
        _scheduleLagMs = Meter.CreateHistogram<double>(
            "acta.schedule.lag",
            "ms",
            "How far past its nominal instant an occurrence stood when its firing was admitted."
        );
        _scheduleDueAgeSeconds = Meter.CreateHistogram<double>(
            "acta.schedule.due.age",
            "s",
            "How long past its evaluation instant each due schedule stood when a pass read it: the overdue frontier."
        );
        _schedulePassMs = Meter.CreateHistogram<double>(
            "acta.schedule.pass",
            "ms",
            "Wall time of one admission pass, its reads, planning, apply, and replans included."
        );
        _scheduleStaleGroups = Meter.CreateCounter<long>(
            "acta.schedule.stale",
            "{group}",
            "Plan groups another writer moved before they applied; each replans on the next try."
        );
        _firingStartDelayMs = Meter.CreateHistogram<double>(
            "acta.firing.start.delay",
            "ms",
            "Time from a firing's admission to the first attempt that ran it, by the worker's clock."
        );
        Meter.CreateObservableGauge(
            "acta.jobs.inflight",
            ObserveInFlight,
            "{job}",
            "Jobs this process holds per namespace, tagged by state: claimed, executing, or finalizing."
        );
    }

    public void RecordExecution(string @namespace, string jobName, string outcome, string? reason, double durationMs)
    {
        var tags = new TagList
        {
            { "namespace", @namespace },
            { "job_name", jobName },
            { "outcome", outcome },
        };

        // Duration excludes reason (a histogram bucket per reason multiplies series for no
        // operator value); the executions counter carries it so failures are sliceable by cause.
        _executionDurationMs.Record(durationMs, tags);

        if (reason is not null)
        {
            tags.Add("reason", reason);
        }

        _executions.Add(1, tags);
    }

    public void RecordClaim(string @namespace, string result, int count = 1) =>
        _claims.Add(count, new TagList { { "namespace", @namespace }, { "result", result } });

    /// <summary>
    /// namespace is the channel's namespace ("*" for all-worker-namespaces, ABSENT for
    /// job-completion channels whose per-job value would explode cardinality); channel is one of
    /// { worker_namespace, all_worker_namespaces, job_completion }; reason is one of
    /// { work_available, horizon_changed, job_finished, unknown }.
    /// </summary>
    public void RecordWakeupPublish(string? @namespace, string channel, string reason)
    {
        var tags = new TagList { { "channel", channel }, { "reason", reason } };
        if (@namespace is not null)
        {
            tags.Add("namespace", @namespace);
        }

        _wakeupPublishes.Add(1, tags);
    }

    public void RecordWakeupPublishFailure(string? @namespace, string channel, string reason, string exceptionType)
    {
        var tags = new TagList
        {
            { "channel", channel },
            { "reason", reason },
            { "exception_type", exceptionType },
        };
        if (@namespace is not null)
        {
            tags.Add("namespace", @namespace);
        }

        _wakeupPublishFailures.Add(1, tags);
    }

    /// <summary>
    /// result is one of { signaled, timed_out }; a signaled wait is a wakeup interrupting an idle
    /// sleep.
    /// </summary>
    public void RecordWakeupWait(string @namespace, string result) =>
        _wakeupWaits.Add(1, new TagList { { "namespace", @namespace }, { "result", result } });

    /// <summary>
    /// outcome is one of { replayed, interrupted, succeeded, failed, exhausted } - the five
    /// RuntimeJobContext actually emits, pinned as a set in ActaMetricsTests. step_name is
    /// deliberately excluded: user-defined, would blow up cardinality; it stays on the log scope.
    /// </summary>
    public void RecordStep(string @namespace, string jobName, string outcome) =>
        _steps.Add(
            1,
            new TagList
            {
                { "namespace", @namespace },
                { "job_name", jobName },
                { "outcome", outcome },
            }
        );

    public void RecordLockReleaseFailure(string @namespace, string jobName, string lockKind, string exceptionType) =>
        _lockReleaseFailures.Add(
            1,
            new TagList
            {
                { "namespace", @namespace },
                { "job_name", jobName },
                { "lock_kind", lockKind },
                { "exception_type", exceptionType },
            }
        );

    /// <summary>
    /// Moves the live count of completion writes that have failed and are still being repeated.
    /// Untagged: the reading is the size of a provider outage this worker is riding out, and every
    /// tag that would slice it (namespace, job name) is already on the log lines the retry emits.
    /// </summary>
    public void RecordUnsettledCompletion(int delta) => _unsettledCompletions.Add(delta);

    /// <summary>How many completions each finalizer call carried: the batching an operator's load actually gets.</summary>
    public void RecordCompletionBatch(string @namespace, int count) =>
        _completionBatches.Record(count, new KeyValuePair<string, object?>("namespace", @namespace));

    /// <summary>outcome is one of { completed, failed }; a failed pass waits out the interval its heartbeat moved.</summary>
    public void RecordRecoveryPass(string @namespace, string outcome) =>
        _recoveryPasses.Add(1, new TagList { { "namespace", @namespace }, { "outcome", outcome } });

    public void RecordAlertProjectionSkip(string @namespace, string reason) =>
        _alertProjectionSkips.Add(1, new TagList { { "namespace", @namespace }, { "reason", reason } });

    public void RecordFencedAttempt(string @namespace, string jobName) =>
        _fencedAttempts.Add(1, new TagList { { "namespace", @namespace }, { "job_name", jobName } });

    /// <summary>A reading near zero is an attempt its worker almost stopped for want of a renewal.</summary>
    public void RecordLeaseHeadroom(string @namespace, TimeSpan headroom) =>
        _leaseHeadroomMs.Record(headroom.TotalMilliseconds, new KeyValuePair<string, object?>("namespace", @namespace));

    /// <summary>The wait an alert-required event had before a pass could project it, the safe horizon included.</summary>
    public void RecordAlertLag(string @namespace, TimeSpan lag) =>
        _alertLagSeconds.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("namespace", @namespace));

    /// <summary>
    /// decision is one of { admitted, skipped, expired, blocked, exhausted, error }, pinned in ActaMetricsTests; one
    /// evaluation may take several (a skipped range before an admitted occurrence), each counted.
    /// </summary>
    public void RecordScheduleDecision(string @namespace, string decision) =>
        _scheduleDecisions.Add(1, new TagList { { "namespace", @namespace }, { "decision", decision } });

    /// <summary>A reading inside the grace is an admitter that arrived in time; past it, a range the misfire policy decided.</summary>
    public void RecordScheduleLag(string @namespace, TimeSpan lag) =>
        _scheduleLagMs.Record(Math.Max(0, lag.TotalMilliseconds), new KeyValuePair<string, object?>("namespace", @namespace));

    /// <summary>Recorded once per due schedule per pass; the maximum is the age of the namespace's overdue frontier.</summary>
    public void RecordScheduleDueAge(string @namespace, TimeSpan age) =>
        _scheduleDueAgeSeconds.Record(Math.Max(0, age.TotalSeconds), new KeyValuePair<string, object?>("namespace", @namespace));

    /// <summary>One pass's wall time, and the plan groups it had to replan because another host moved them first.</summary>
    public void RecordSchedulePass(string @namespace, TimeSpan elapsed, int staleGroups)
    {
        var tag = new KeyValuePair<string, object?>("namespace", @namespace);
        _schedulePassMs.Record(elapsed.TotalMilliseconds, tag);
        if (staleGroups > 0)
        {
            _scheduleStaleGroups.Add(staleGroups, tag);
        }
    }

    /// <summary>The admission instant is the database clock and the start the worker's, so the reading carries their skew; a negative one reads as zero.</summary>
    public void RecordFiringStartDelay(string @namespace, string jobName, TimeSpan delay) =>
        _firingStartDelayMs.Record(
            Math.Max(0, delay.TotalMilliseconds),
            new TagList { { "namespace", @namespace }, { "job_name", jobName } }
        );

    public void AddInFlightSource(string @namespace, Func<InFlightJobState, int> liveCount)
    {
        lock (_inFlightSources)
        {
            _inFlightSources.Add((@namespace, liveCount));
        }
    }

    private IEnumerable<Measurement<int>> ObserveInFlight()
    {
        lock (_inFlightSources)
        {
            return
            [
                .. _inFlightSources.SelectMany(source =>
                    InFlightStates.Select(state => new Measurement<int>(
                        source.LiveCount(state.State),
                        new KeyValuePair<string, object?>("namespace", source.Namespace),
                        new KeyValuePair<string, object?>("state", state.Tag)
                    ))
                ),
            ];
        }
    }

    public void Dispose() => Meter.Dispose();
}
