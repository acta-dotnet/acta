using System.Diagnostics.Metrics;
using Acta.Modules.Execution;
using Acta.Modules.Execution.Workers;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The wake contract, no DB: <see cref="AsyncWakeSignal"/> auto-reset + single-slot latch
/// coalescing, <see cref="InProcessWakeup"/> channel fan-out (worker-namespace allocate-on-publish,
/// job-completion waiter-managed lifetime, all-worker-namespaces enumeration),
/// <see cref="WorkerLoop.ComputeSleep"/> deadline math (floor, jitter, hard safety cap), the
/// <see cref="WorkerWakeupPublisher"/> never-breaks-the-caller guarantee, and the
/// <see cref="WorkerWakeupChannel"/> reserved-name rules.
/// </summary>
public sealed class WorkerWakeupTests
{
    private static readonly TimeSpan WaitGenerously = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WaitBriefly = TimeSpan.FromMilliseconds(50);
    private static readonly CancellationToken None = CancellationToken.None;

    // ---- AsyncWakeSignal ----

    [Fact]
    public async Task Set_before_wait_latches_and_satisfies_exactly_one_wait()
    {
        var signal = new AsyncWakeSignal();
        signal.Set();

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await signal.WaitAsync(WaitBriefly, None));
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await signal.WaitAsync(WaitBriefly, None));
    }

    [Fact]
    public async Task Many_sets_between_waits_coalesce_onto_one_latch_slot()
    {
        var signal = new AsyncWakeSignal();
        signal.Set();
        signal.Set();
        signal.Set();

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await signal.WaitAsync(WaitBriefly, None));
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await signal.WaitAsync(WaitBriefly, None));
    }

    [Fact]
    public async Task Set_completes_a_pending_wait_without_waiting_out_the_timeout()
    {
        var signal = new AsyncWakeSignal();
        var wait = signal.WaitAsync(WaitGenerously, None).AsTask();
        Assert.False(wait.IsCompleted);

        signal.Set();

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await wait.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task Wait_times_out_when_nothing_signals()
    {
        var signal = new AsyncWakeSignal();

        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await signal.WaitAsync(WaitBriefly, None));
    }

    [Fact]
    public async Task Wait_throws_on_cancellation()
    {
        var signal = new AsyncWakeSignal();
        using var cts = new CancellationTokenSource();
        var wait = signal.WaitAsync(WaitGenerously, cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(WaitGenerously, None));
    }

    // ---- InProcessWakeup: worker-namespace channels ----

    [Fact]
    public async Task Namespace_wake_wakes_that_namespaces_waiter_only()
    {
        var wakeup = new InProcessWakeup();
        var billing = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();
        var shipping = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("shipping"), WaitBriefly, None).AsTask();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.WorkerNamespace("billing"),
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await billing.WaitAsync(WaitGenerously, None));
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await shipping.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task All_worker_namespaces_wake_reaches_a_waiter_per_known_namespace()
    {
        var wakeup = new InProcessWakeup();
        var billing = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();
        var shipping = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("shipping"), WaitGenerously, None).AsTask();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.AllWorkerNamespaces,
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await billing.WaitAsync(WaitGenerously, None));
        Assert.Equal(WorkerWakeupWaitResult.Signaled, await shipping.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task All_worker_namespaces_wake_ahead_of_any_wait_is_lossy_by_contract()
    {
        var wakeup = new InProcessWakeup();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.AllWorkerNamespaces,
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorkerWakeupWaitResult.TimedOut,
            await wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitBriefly, None)
        );
    }

    [Fact]
    public async Task Namespace_wake_ahead_of_the_first_wait_latches()
    {
        var wakeup = new InProcessWakeup();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.WorkerNamespace("billing"),
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            WorkerWakeupWaitResult.Signaled,
            await wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitBriefly, None)
        );
    }

    // ---- InProcessWakeup: job-completion channels (unbounded keyspace) ----

    [Fact]
    public async Task Job_completion_wake_reaches_a_pending_waiter()
    {
        var wakeup = new InProcessWakeup();
        var wait = wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(42), WaitGenerously, None).AsTask();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.JobCompletion(42),
            WorkerWakeupReason.JobFinished,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await wait.WaitAsync(WaitGenerously, None));
    }

    [Fact]
    public async Task Job_completion_wake_without_a_waiter_does_not_latch()
    {
        var wakeup = new InProcessWakeup();

        // No allocate-on-publish for the unbounded job keyspace: a pre-wait wake is lost by
        // contract (the waiter's poll floor covers it) instead of leaking one entry per job.
        await wakeup.WakeAsync(
            WorkerWakeupChannel.JobCompletion(42),
            WorkerWakeupReason.JobFinished,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(42), WaitBriefly, None));
    }

    [Fact]
    public async Task Job_completion_entry_is_removed_when_its_last_waiter_leaves()
    {
        var wakeup = new InProcessWakeup();

        // First wait times out and removes the entry; a wake after that finds nothing to latch on,
        // so a second wait times out too - proving the per-job entry does not outlive its waiters.
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(42), WaitBriefly, None));
        await wakeup.WakeAsync(
            WorkerWakeupChannel.JobCompletion(42),
            WorkerWakeupReason.JobFinished,
            TestContext.Current.CancellationToken
        );
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(42), WaitBriefly, None));
    }

    [Fact]
    public async Task All_worker_namespaces_wake_does_not_reach_job_completion_waiters()
    {
        var wakeup = new InProcessWakeup();
        var jobWait = wakeup.WaitAsync(WorkerWakeupChannel.JobCompletion(42), WaitBriefly, None).AsTask();
        var nsWait = wakeup.WaitAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WaitGenerously, None).AsTask();

        await wakeup.WakeAsync(
            WorkerWakeupChannel.AllWorkerNamespaces,
            WorkerWakeupReason.WorkAvailable,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WorkerWakeupWaitResult.Signaled, await nsWait.WaitAsync(WaitGenerously, None));
        Assert.Equal(WorkerWakeupWaitResult.TimedOut, await jobWait.WaitAsync(WaitGenerously, None));
    }

    // ---- WorkerLoop.ComputeSleep ----

    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Jitter = TimeSpan.FromMilliseconds(100);
    private static readonly DateTime DbNow = new(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ComputeSleep_with_no_horizon_returns_the_safety_interval()
    {
        Assert.Equal(Safety, WorkerLoop.ComputeSleep(null, Safety, Floor, Jitter));
    }

    [Fact]
    public void ComputeSleep_with_no_ready_rows_returns_the_safety_interval()
    {
        var horizon = new ClaimHorizon(DbNow, NextReadyAtUtc: null);

        Assert.Equal(Safety, WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter));
    }

    [Fact]
    public void ComputeSleep_with_a_due_horizon_returns_the_floor()
    {
        // Due rows existed but were locked away by another worker mid-claim - quick retry, not a
        // safety-interval sleep.
        var horizon = new ClaimHorizon(DbNow, DbNow.AddSeconds(-1));

        Assert.Equal(Floor, WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter));
    }

    [Fact]
    public void ComputeSleep_clamps_a_near_deadline_up_to_the_floor()
    {
        var horizon = new ClaimHorizon(DbNow, DbNow.AddMilliseconds(1));

        var sleep = WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter);

        Assert.InRange(sleep, Floor, Floor + Jitter);
    }

    [Fact]
    public void ComputeSleep_sleeps_until_the_deadline_plus_bounded_jitter()
    {
        var untilDue = TimeSpan.FromSeconds(3);
        var horizon = new ClaimHorizon(DbNow, DbNow + untilDue);

        var sleep = WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter);

        Assert.InRange(sleep, untilDue, untilDue + Jitter);
    }

    [Fact]
    public void ComputeSleep_without_jitter_is_exactly_the_deadline_delta()
    {
        var untilDue = TimeSpan.FromSeconds(3);
        var horizon = new ClaimHorizon(DbNow, DbNow + untilDue);

        Assert.Equal(untilDue, WorkerLoop.ComputeSleep(horizon, Safety, Floor, TimeSpan.Zero));
    }

    [Fact]
    public void ComputeSleep_never_exceeds_the_safety_interval()
    {
        // The cap applies AFTER jitter: a deadline just inside the safety interval cannot be
        // jittered past it, so SafetyPollInterval stays the hard upper bound on idle sleep.
        var horizon = new ClaimHorizon(DbNow, DbNow + Safety - TimeSpan.FromMilliseconds(1));

        for (var i = 0; i < 50; i++)
        {
            Assert.True(WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter) <= Safety);
        }
    }

    [Fact]
    public void ComputeSleep_with_a_deadline_past_the_safety_interval_returns_the_safety_interval()
    {
        var horizon = new ClaimHorizon(DbNow, DbNow.AddHours(2));

        Assert.Equal(Safety, WorkerLoop.ComputeSleep(horizon, Safety, Floor, Jitter));
    }

    // ---- WorkerWakeupPublisher ----

    [Fact]
    public async Task A_throwing_transport_never_breaks_the_publishing_caller()
    {
        using var metrics = new JobMetrics();
        var publisher = new WorkerWakeupPublisher(new ThrowingWakeup(), metrics: metrics);
        var captured = NewCapture();
        using (CreateListener(metrics, "acta.wakeup.publish.failures", captured))
        {
            await publisher.WakeAsync(
                WorkerWakeupChannel.WorkerNamespace("billing"),
                WorkerWakeupReason.WorkAvailable,
                TestContext.Current.CancellationToken
            );
        }

        var failure = Assert.Single(captured);
        Assert.Equal("billing", failure.Tags["namespace"]);
        Assert.Equal("worker_namespace", failure.Tags["channel"]);
        Assert.Equal("InvalidOperationException", failure.Tags["exception_type"]);
    }

    [Fact]
    public async Task A_cancelled_caller_token_never_breaks_the_publishing_caller()
    {
        // Every wake is published after its durable mutation committed; surfacing the caller's
        // cancellation here would report failure for an operation that already succeeded.
        var publisher = new WorkerWakeupPublisher(new CancellationHonoringWakeup());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await publisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace("billing"), WorkerWakeupReason.WorkAvailable, cts.Token);
    }

    [Fact]
    public async Task Publisher_records_an_attempt_per_wake()
    {
        using var metrics = new JobMetrics();
        var publisher = new WorkerWakeupPublisher(new InProcessWakeup(), metrics: metrics);
        var captured = NewCapture();
        using (CreateListener(metrics, "acta.wakeup.publish.attempts", captured))
        {
            await publisher.WakeAsync(
                WorkerWakeupChannel.AllWorkerNamespaces,
                WorkerWakeupReason.HorizonChanged,
                TestContext.Current.CancellationToken
            );
        }

        var attempt = Assert.Single(captured);
        Assert.Equal("*", attempt.Tags["namespace"]);
        Assert.Equal("all_worker_namespaces", attempt.Tags["channel"]);
        Assert.Equal("horizon_changed", attempt.Tags["reason"]);
    }

    [Fact]
    public async Task Job_completion_wake_carries_no_namespace_tag()
    {
        using var metrics = new JobMetrics();
        var publisher = new WorkerWakeupPublisher(new InProcessWakeup(), metrics: metrics);
        var captured = NewCapture();
        using (CreateListener(metrics, "acta.wakeup.publish.attempts", captured))
        {
            await publisher.WakeAsync(
                WorkerWakeupChannel.JobCompletion(42),
                WorkerWakeupReason.JobFinished,
                TestContext.Current.CancellationToken
            );
        }

        var attempt = Assert.Single(captured);
        Assert.DoesNotContain("namespace", attempt.Tags.Keys);
        Assert.Equal("job_completion", attempt.Tags["channel"]);
        Assert.Equal("job_finished", attempt.Tags["reason"]);
    }

    // ---- WorkerWakeupChannel ----

    [Fact]
    public void Channel_factories_enforce_the_reserved_and_canonical_names()
    {
        Assert.Throws<ArgumentException>(() => WorkerWakeupChannel.WorkerNamespace("*"));
        Assert.Throws<ArgumentException>(() => WorkerWakeupChannel.WorkerNamespace(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerWakeupChannel.JobCompletion(0));

        Assert.Equal("ns:billing", WorkerWakeupChannel.WorkerNamespace("billing").Name);
        Assert.Equal("job:42", WorkerWakeupChannel.JobCompletion(42).Name);
        Assert.Equal("*", WorkerWakeupChannel.AllWorkerNamespaces.Name);
        Assert.Equal("*", default(WorkerWakeupChannel).Name);
        Assert.Equal(WorkerWakeupChannelKind.AllWorkerNamespaces, default(WorkerWakeupChannel).Kind);

        Assert.True(WorkerWakeupChannel.WorkerNamespace("billing").AllocatesOnPublish);
        Assert.True(WorkerWakeupChannel.AllWorkerNamespaces.AllocatesOnPublish);
        Assert.False(WorkerWakeupChannel.JobCompletion(42).AllocatesOnPublish);
    }

    private sealed class CancellationHonoringWakeup : IWorkerWakeup
    {
        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("wait not used");
    }

    private sealed class ThrowingWakeup : IWorkerWakeup
    {
        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("transport down");

        public ValueTask<WorkerWakeupWaitResult> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("transport down");
    }

    private static List<(long Value, IReadOnlyDictionary<string, object?> Tags)> NewCapture() => [];

    // Captures every measurement the named instrument emits while the listener is alive, mirroring
    // JobMetricsTests.Collect but disposable so an awaited act can run inside the capture window.
    // Scoped to THIS test's meter instance - parallel test classes share instrument names and tags,
    // so a name-filtered listener would cross-capture their measurements.
    private static MeterListener CreateListener(
        JobMetrics metrics,
        string instrumentName,
        List<(long Value, IReadOnlyDictionary<string, object?> Tags)> captured
    )
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (ReferenceEquals(inst.Meter, metrics.Meter) && inst.Name == instrumentName)
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (_, value, tags, _) => captured.Add((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)))
        );
        listener.Start();
        return listener;
    }
}
