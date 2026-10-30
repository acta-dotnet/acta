using System.Diagnostics.Metrics;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The <c>Acta</c> meter contract: instrument names, tag keys, and tag values are the
/// operator-facing metric surface a dashboard query binds to. Tags stay low-cardinality
/// (namespace / job_name / outcome / reason / result); job id and execution number are
/// deliberately absent (they live on the logging scope and traces, not metric tags).
/// </summary>
public sealed class ActaMetricsTests
{
    [Fact]
    public void RecordExecution_increments_executions_counter_with_tags()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "succeeded", reason: null, durationMs: 12)
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal("succeeded", Tags["outcome"]);
    }

    [Fact]
    public void RecordExecution_records_duration_histogram_without_reason_tag()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<double>(
                metrics,
                "acta.duration",
                () => metrics.RecordExecution("billing", "send-receipt", "failed", reason: "unhandled-exception", durationMs: 42)
            )
        );

        Assert.Equal(42, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("failed", Tags["outcome"]);
        Assert.DoesNotContain("reason", Tags.Keys);
    }

    [Fact]
    public void RecordExecution_adds_reason_tag_to_executions_when_present()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "failed", reason: "unhandled-exception", durationMs: 1)
            )
        );

        Assert.Equal("unhandled-exception", Tags["reason"]);
    }

    [Fact]
    public void RecordExecution_omits_reason_tag_on_executions_when_absent()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.executions",
                () => metrics.RecordExecution("billing", "send-receipt", "succeeded", reason: null, durationMs: 1)
            )
        );

        Assert.DoesNotContain("reason", Tags.Keys);
    }

    [Fact]
    public void RecordClaim_increments_claims_counter_with_result_tag()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(Collect<long>(metrics, "acta.claims", () => metrics.RecordClaim("billing", "nothing-claimed")));

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("nothing-claimed", Tags["result"]);
    }

    // The frozen surface here is the instrument itself: the "acta.steps" name, its "{step}" unit, and
    // the tag KEYS a dashboard query binds to. The outcome values are pinned only as the set
    // RuntimeJobContext.RunStepImplAsync is known to emit - this test supplies each string itself, so
    // a renamed or dropped outcome in the producer cannot fail here. Catching that would take a test
    // that drives real steps while listening to the meter; the conformance specs that drive real
    // steps assert behavior, not metric tags.
    [Theory]
    [InlineData("succeeded")]
    [InlineData("replayed")]
    [InlineData("interrupted")]
    [InlineData("failed")]
    [InlineData("exhausted")]
    public void RecordStep_increments_steps_counter_with_outcome_tag(string outcome)
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.steps", () => metrics.RecordStep("billing", "send-receipt", outcome))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal(outcome, Tags["outcome"]);
        Assert.Equal("{step}", CollectUnit(metrics, "acta.steps"));
    }

    [Fact]
    public void RecordWakeupPublish_increments_attempts_counter_with_channel_and_reason_tags()
    {
        using var metrics = new ActaMetrics();

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
        using var metrics = new ActaMetrics();

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
        using var metrics = new ActaMetrics();

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
        using var metrics = new ActaMetrics();

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
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(
                metrics,
                "acta.lock.release.failures",
                () => metrics.RecordLockReleaseFailure("billing", "send-receipt", "concurrency_key", "TimeoutException")
            )
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal("concurrency_key", Tags["lock_kind"]);
        Assert.Equal("TimeoutException", Tags["exception_type"]);
    }

    [Fact]
    public void RecordAlertProjectionSkip_increments_counter_with_namespace_and_reason()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.alert.projection.skips", () => metrics.RecordAlertProjectionSkip("billing", "unknown-job"))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("unknown-job", Tags["reason"]);
    }

    [Fact]
    public void RecordCompletionBatch_records_the_batch_size_histogram_with_the_namespace_tag()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<int>(metrics, "acta.completions.batch", () => metrics.RecordCompletionBatch("billing", 37))
        );

        Assert.Equal(37, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("{completion}", CollectUnit(metrics, "acta.completions.batch"));
    }

    [Fact]
    public void RecordFencedAttempt_increments_the_fenced_counter_with_namespace_and_job_name()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.executions.fenced", () => metrics.RecordFencedAttempt("billing", "send-receipt"))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("send-receipt", Tags["job_name"]);
        Assert.Equal("{execution}", CollectUnit(metrics, "acta.executions.fenced"));
    }

    [Fact]
    public void RecordLeaseHeadroom_records_milliseconds_with_the_namespace_tag()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<double>(metrics, "acta.lease.headroom", () => metrics.RecordLeaseHeadroom("billing", TimeSpan.FromSeconds(12.5)))
        );

        Assert.Equal(12_500, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("ms", CollectUnit(metrics, "acta.lease.headroom"));
    }

    [Fact]
    public void RecordAlertLag_records_seconds_with_the_namespace_tag()
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<double>(metrics, "acta.alerts.lag", () => metrics.RecordAlertLag("billing", TimeSpan.FromMinutes(2)))
        );

        Assert.Equal(120, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal("s", CollectUnit(metrics, "acta.alerts.lag"));
    }

    [Fact]
    public void In_flight_gauge_reports_each_registered_namespace_by_state()
    {
        using var metrics = new ActaMetrics();
        metrics.AddInFlightSource(
            "billing",
            state =>
                state switch
                {
                    InFlightJobState.Claimed => 2,
                    InFlightJobState.Executing => 3,
                    _ => 1,
                }
        );
        metrics.AddInFlightSource("shipping", _ => 0);

        var measurements = Collect<int>(metrics, "acta.jobs.inflight", act: null, observable: true);

        int Read(string ns, string state) =>
            measurements.Single(m => (string?)m.Tags["namespace"] == ns && (string?)m.Tags["state"] == state).Value;
        Assert.Equal(6, measurements.Count);
        Assert.Equal(2, Read("billing", "claimed"));
        Assert.Equal(3, Read("billing", "executing"));
        Assert.Equal(1, Read("billing", "finalizing"));
        Assert.Equal(0, Read("shipping", "executing"));
    }

    // The decision tag's values are the ones ScheduleAdmitter.Decide emits, pinned as a set: a dashboard
    // query binds to each name.
    [Theory]
    [InlineData("admitted")]
    [InlineData("skipped")]
    [InlineData("expired")]
    [InlineData("blocked")]
    [InlineData("exhausted")]
    [InlineData("error")]
    public void RecordScheduleDecision_increments_the_decisions_counter_with_the_decision_tag(string decision)
    {
        using var metrics = new ActaMetrics();

        var (Value, Tags) = Assert.Single(
            Collect<long>(metrics, "acta.schedule.decisions", () => metrics.RecordScheduleDecision("billing", decision))
        );

        Assert.Equal(1, Value);
        Assert.Equal("billing", Tags["namespace"]);
        Assert.Equal(decision, Tags["decision"]);
        Assert.Equal("{decision}", CollectUnit(metrics, "acta.schedule.decisions"));
    }

    [Fact]
    public void RecordScheduleLag_and_due_age_record_the_namespace_tag_and_floor_at_zero()
    {
        using var metrics = new ActaMetrics();

        var lag = Assert.Single(
            Collect<double>(metrics, "acta.schedule.lag", () => metrics.RecordScheduleLag("billing", TimeSpan.FromMilliseconds(250)))
        );
        var early = Assert.Single(
            Collect<double>(metrics, "acta.schedule.lag", () => metrics.RecordScheduleLag("billing", TimeSpan.FromSeconds(-3)))
        );
        var age = Assert.Single(
            Collect<double>(metrics, "acta.schedule.due.age", () => metrics.RecordScheduleDueAge("billing", TimeSpan.FromMinutes(1.5)))
        );

        Assert.Equal(250, lag.Value);
        Assert.Equal(0, early.Value);
        Assert.Equal(90, age.Value);
        Assert.Equal("billing", lag.Tags["namespace"]);
        Assert.Equal("billing", age.Tags["namespace"]);
        Assert.Equal("ms", CollectUnit(metrics, "acta.schedule.lag"));
        Assert.Equal("s", CollectUnit(metrics, "acta.schedule.due.age"));
    }

    [Fact]
    public void RecordSchedulePass_records_the_wall_time_and_counts_stale_groups_only_when_there_are_any()
    {
        using var metrics = new ActaMetrics();

        var pass = Assert.Single(
            Collect<double>(metrics, "acta.schedule.pass", () => metrics.RecordSchedulePass("billing", TimeSpan.FromMilliseconds(12), 0))
        );
        var quiet = Collect<long>(
            metrics,
            "acta.schedule.stale",
            () => metrics.RecordSchedulePass("billing", TimeSpan.FromMilliseconds(12), 0)
        );
        var stale = Assert.Single(
            Collect<long>(metrics, "acta.schedule.stale", () => metrics.RecordSchedulePass("billing", TimeSpan.FromMilliseconds(40), 2))
        );

        Assert.Equal(12, pass.Value);
        Assert.Equal("billing", pass.Tags["namespace"]);
        Assert.Empty(quiet);
        Assert.Equal(2, stale.Value);
        Assert.Equal("{group}", CollectUnit(metrics, "acta.schedule.stale"));
        Assert.Equal("ms", CollectUnit(metrics, "acta.schedule.pass"));
    }

    [Fact]
    public void RecordFiringStartDelay_records_milliseconds_with_namespace_and_job_name_and_floors_skew_at_zero()
    {
        using var metrics = new ActaMetrics();

        var delay = Assert.Single(
            Collect<double>(
                metrics,
                "acta.firing.start.delay",
                () => metrics.RecordFiringStartDelay("billing", "send-receipt", TimeSpan.FromMilliseconds(80))
            )
        );
        var skewed = Assert.Single(
            Collect<double>(
                metrics,
                "acta.firing.start.delay",
                () => metrics.RecordFiringStartDelay("billing", "send-receipt", TimeSpan.FromMilliseconds(-5))
            )
        );

        Assert.Equal(80, delay.Value);
        Assert.Equal(0, skewed.Value);
        Assert.Equal("billing", delay.Tags["namespace"]);
        Assert.Equal("send-receipt", delay.Tags["job_name"]);
        Assert.Equal("ms", CollectUnit(metrics, "acta.firing.start.delay"));
    }

    // Captures every measurement the named instrument emits while act runs (or, for an observable
    // instrument, on a single forced collection). Tags are projected to a dictionary so assertions read
    // by key, mirroring JobLogScopeTests.Fields. Scoped to THIS test's meter instance - parallel test
    // classes share instrument names and tags, so a name-filtered listener would cross-capture.
    private static List<(T Value, IReadOnlyDictionary<string, object?> Tags)> Collect<T>(
        ActaMetrics metrics,
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

    // Captures an instrument's declared unit without listening for measurements. Pairs with Collect
    // where a fact also pins the unit alongside tags; instruments freeze at 1.0, unit included.
    private static string? CollectUnit(ActaMetrics metrics, string instrumentName)
    {
        string? unit = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, _) =>
        {
            if (ReferenceEquals(inst.Meter, metrics.Meter) && inst.Name == instrumentName)
            {
                unit = inst.Unit;
            }
        };
        listener.Start();
        return unit;
    }
}
