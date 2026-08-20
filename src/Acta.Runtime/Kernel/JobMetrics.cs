using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Acta.Runtime.Kernel;

/// <summary>
/// Owns the <c>Acta</c> meter and its instruments. A process-wide singleton: the runtime emits
/// one execution measurement per durable completion (in <see cref="Acta.Runtime.Modules.Execution.JobExecution"/> for Direct/Buffered,
/// in <see cref="Acta.Runtime.Modules.Execution.CompletionSink"/> at flush time for Bulk), one claim
/// measurement per claim attempt in <see cref="Acta.Runtime.Modules.Execution.JobExecutor"/>, and observes the live in-flight count.
/// Counters are additive: the backend aggregates and slices by tag, so no running totals are kept
/// in process.
/// </summary>
/// <remarks>
/// Tag keys and values are the operator-facing metric surface, pinned by <c>JobMetricsTests</c>. Tags
/// are kept low-cardinality (namespace / job_name / outcome / reason_code / result); the unbounded job
/// id and execution number stay off metrics and live on the logging scope and traces instead.
/// </remarks>
internal sealed class JobMetrics : IDisposable
{
    public const string MeterName = "Acta";

    // Exposed so tests can scope a MeterListener to THIS instance's meter; same-named meters from
    // parallel test hosts would otherwise cross-capture identical instrument/tag measurements.
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

    // Each worker registers its namespace's live in-flight count; the observable gauge reads them on
    // collection. Reading live state beats maintaining a running total that can drift on a crash.
    private readonly List<(string Namespace, Func<int> LiveCount)> _executingSources = [];

    public JobMetrics()
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
        Meter.CreateObservableGauge("acta.executing", ObserveExecuting, "{job}", "Currently executing jobs per namespace.");
    }

    public void RecordExecution(string @namespace, string jobName, string outcome, string? reasonCode, double durationMs)
    {
        var tags = new TagList
        {
            { "namespace", @namespace },
            { "job_name", jobName },
            { "outcome", outcome },
        };

        // Duration excludes reason_code (a histogram bucket per reason multiplies series for no
        // operator value); the executions counter carries it so failures are sliceable by cause.
        _executionDurationMs.Record(durationMs, tags);

        if (reasonCode is not null)
        {
            tags.Add("reason_code", reasonCode);
        }

        _executions.Add(1, tags);
    }

    public void RecordClaim(string @namespace, string result) =>
        _claims.Add(1, new TagList { { "namespace", @namespace }, { "result", result } });

    // namespace is the channel's namespace ("*" for all-worker-namespaces, ABSENT for job-completion
    // channels whose per-job value would explode cardinality); channel is one of { worker_namespace,
    // all_worker_namespaces, job_completion }; reason is one of { work_available, horizon_changed,
    // job_finished, unknown }.
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

    // result is one of { signaled, timed_out }; a signaled wait is a wakeup interrupting an idle sleep.
    public void RecordWakeupWait(string @namespace, string result) =>
        _wakeupWaits.Add(1, new TagList { { "namespace", @namespace }, { "result", result } });

    // outcome is one of { replayed, interrupted, succeeded, failed, exhausted } - the five RuntimeJobContext
    // actually emits, pinned as a set in JobMetricsTests. step_name is
    // deliberately excluded: it is user-defined and would blow up cardinality; it stays on the log scope.
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

    public void RecordAlertProjectionSkip(string @namespace, string reason) =>
        _alertProjectionSkips.Add(1, new TagList { { "namespace", @namespace }, { "reason", reason } });

    public void AddExecutingSource(string @namespace, Func<int> liveCount)
    {
        lock (_executingSources)
        {
            _executingSources.Add((@namespace, liveCount));
        }
    }

    private IEnumerable<Measurement<int>> ObserveExecuting()
    {
        lock (_executingSources)
        {
            var snapshot = new Measurement<int>[_executingSources.Count];
            for (var i = 0; i < _executingSources.Count; i++)
            {
                var (ns, count) = _executingSources[i];
                snapshot[i] = new Measurement<int>(count(), new KeyValuePair<string, object?>("namespace", ns));
            }

            return snapshot;
        }
    }

    public void Dispose() => Meter.Dispose();
}
