using System.Diagnostics.Metrics;
using Acta.Runtime.Kernel;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The <c>Acta</c> meter contract: instrument names, tag keys, and tag values are the
/// operator-facing metric surface a dashboard query binds to. Tags stay low-cardinality
/// (namespace / job_name / outcome / reason_code / result); job id and execution number are
/// deliberately absent (they live on the logging scope and traces, not metric tags).
/// </summary>
public sealed class JobMetricsTests
{
    [Fact]
    public void RecordExecution_increments_executions_counter_with_tags()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "succeeded", reasonCode: null, durationMs: 12)
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal("succeeded", Tags["outcome"]);
    }

    [Fact]
    public void RecordExecution_records_duration_histogram_without_reason_code_tag()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<double>(
                metrics,
                "acta.duration",
                () => metrics.RecordExecution("billing", "send-receipt", "failed", reasonCode: "unhandled-exception", durationMs: 42)
            )
        );

        Assert.Equal(42, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("failed", Tags["outcome"]);
        Assert.DoesNotContain("reason_code", Tags.Keys);
    }

    [Fact]
    public void RecordExecution_adds_reason_code_tag_to_executions_when_present()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "failed", reasonCode: "unhandled-exception", durationMs: 1)
            )
        );

        Assert.Equal("unhandled-exception", Tags["reason_code"]);
    }

    [Fact]
    public void RecordExecution_omits_reason_code_tag_on_executions_when_absent()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "succeeded", reasonCode: null, durationMs: 1)
            )
        );

        Assert.DoesNotContain("reason_code", Tags.Keys);
    }

    [Fact]
    public void RecordClaim_increments_claims_counter_with_result_tag()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(Collect<long>(metrics, "acta.claims", () => metrics.RecordClaim("billing", "nothing-claimed")));

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("nothing-claimed", Tags["result"]);
    }

    [Fact]
    public void RecordWakeupPublish_increments_attempts_counter_with_channel_and_reason_tags()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.wakeup.publish.attempts",
                () => metrics.RecordWakeupPublish("billing", "worker_namespace", "work_available")
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("worker_namespace", Tags["channel"]);
        Assert.Equal("work_available", Tags["reason"]);
    }

    [Fact]
    public void RecordWakeupPublish_omits_the_namespace_tag_for_job_completion_channels()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.wakeup.publish.attempts",
                () => metrics.RecordWakeupPublish(@namespace: null, "job_completion", "job_finished")
            )
        );

        Assert.DoesNotContain("namespace", Tags.Keys);
        Assert.Equal("job_completion", Tags["channel"]);
    }

    [Fact]
    public void RecordWakeupPublishFailure_increments_failures_counter_with_exception_type_tag()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.wakeup.publish.failures",
                () => metrics.RecordWakeupPublishFailure("*", "all_worker_namespaces", "horizon_changed", "TimeoutException")
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("*", Tags["namespace"]);
        Assert.Equal("all_worker_namespaces", Tags["channel"]);
        Assert.Equal("horizon_changed", Tags["reason"]);
        Assert.Equal("TimeoutException", Tags["exception_type"]);
    }

    [Fact]
    public void RecordWakeupWait_increments_waits_counter_with_result_tag()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.wakeup.waits", () => metrics.RecordWakeupWait("billing", "signaled"))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("signaled", Tags["result"]);
    }

    [Fact]
    public void RecordLockReleaseFailure_increments_counter_with_bounded_tags()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.lock.release.failures",
                () => metrics.RecordLockReleaseFailure("billing", "send-receipt", "exclusive_key", "TimeoutException")
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal("exclusive_key", Tags["lock_kind"]);
        Assert.Equal("TimeoutException", Tags["exception_type"]);
    }

    [Fact]
    public void RecordAlertProjectionSkip_increments_counter_with_namespace_and_reason()
    {
        using var metrics = new JobMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.alert.projection.skips", () => metrics.RecordAlertProjectionSkip("billing", "unknown-job"))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("unknown-job", Tags["reason"]);
    }

    [Fact]
    public void Executing_gauge_observes_each_registered_namespace_source()
    {
        using var metrics = new JobMetrics();
        metrics.AddExecutingSource("billing", () => 3);
        metrics.AddExecutingSource("shipping", () => 0);

        var measurements = Collect<int>(metrics, "acta.executing", act: null, observable: true);

        Assert.Equal(3, measurements.Single(m => (string?)m.Tags["namespace"] == "billing").Value);
        Assert.Equal(0, measurements.Single(m => (string?)m.Tags["namespace"] == "shipping").Value);
    }

    // Captures every measurement the named instrument emits while act runs (or, for an observable
    // instrument, on a single forced collection). Tags are projected to a dictionary so assertions read
    // by key, mirroring JobLogScopeTests.Fields. Scoped to THIS test's meter instance - parallel test
    // classes share instrument names and tags, so a name-filtered listener would cross-capture.
    private static List<(T Value, IReadOnlyDictionary<string, object?> Tags)> Collect<T>(
        JobMetrics metrics,
        string instrumentName,
        Action? act,
        bool observable = false
    )
        where T : struct
    {
        var captured = new List<(T, IReadOnlyDictionary<string, object?>)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, metrics.Meter) && inst.Name == instrumentName)
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<T>(
            (_, value, tags, _) => captured.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)))
        );
        listener.Start();
        act?.Invoke();
        if (observable)
        {
            listener.RecordObservableInstruments();
        }
        listener.Dispose();
        return captured;
    }
}
