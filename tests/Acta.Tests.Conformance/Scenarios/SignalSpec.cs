using Acta.Configuration;
using Acta.Features.Execution;
using Acta.Features.Signals;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable signals: <c>ctx.WaitSignalAsync</c> suspends the Job on a Pending slot and
/// <c>IJobs.RaiseSignalAsync</c> sets the slot (last-writer-wins) and releases a Suspended Job to Ready.
/// Unlike sleep, a signal wait lands real <c>Suspended</c> with no <c>NextRunAtUtc</c> - only a raise
/// re-arms it.
/// </summary>
[ConformanceSpec(
    "signals.wait-and-raise",
    "Wait suspends a job and a raise releases it last-writer-wins",
    Area = "Signals",
    Contract = "WaitSignalAsync suspends a job on a Pending slot and RaiseSignalAsync sets the slot last-writer-wins releasing only a Suspended job to Ready.",
    Arrange = "Waiting handlers are registered with system jobs disabled and a long safety poll so wake-on-raise is attributable.",
    Act = "Signals are raised after and before waits with typed and presence payloads, duplicates, and against paused, terminal and unknown jobs.",
    Assert = "A wait suspends the job with no NextRunAtUtc and a raise sets the slot last-writer-wins, releasing only a Suspended job to Ready."
)]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.WaitSignalAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.RaiseSignalAsync))]
public abstract class SignalSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // The wake-on-raise fact runs the real loop: a long safety poll + no system recurring slots
    // make a fast pickup attributable to the raise's wakeup publish alone. The RunOnce-driven facts
    // are unaffected by either setting.
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o =>
        {
            o.RegisterFrameworkJobs = false;
            o.SafetyPollInterval = TimeSpan.FromSeconds(20);
        });
    }

    [Fact(DisplayName = "Raise wakes an idle loop to run the released job")]
    public async Task Raise_signal_wakes_an_idle_loop_to_run_the_released_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);

        // Start the loop on a namespace with no Ready rows: its first empty claim sees a null
        // horizon and commits to the full 20s safety sleep.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = Runtime.RunLoopAsync(loopCts.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        var raisedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline && (await ReadJobAsync(enqueued.JobId, ct)).Status != JobStatusCode.Done)
        {
            await Task.Delay(50, ct);
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(raisedAt);

        await loopCts.CancelAsync();
        await loop;

        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        // Far inside the 20s safety sleep the loop was committed to - only the raise's wakeup
        // publish can have interrupted it.
        Assert.True(elapsed < TimeSpan.FromSeconds(8), $"Released job ran after {elapsed}: the raise did not wake the idle loop.");
    }

    [Fact(DisplayName = "Wait lands Suspended with a Pending slot and no NextRunAtUtc")]
    public async Task Wait_before_signal_suspends_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Null(job.NextRunAtUtc);

        var sig = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal("go", sig.Name);
        Assert.Equal(JobCheckpointStateCode.Pending, sig.State);

        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.before", ct));
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Suspended, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobSuspended, ct));
    }

    [Fact(DisplayName = "Wait is idempotent while pending, not duplicating the slot or consuming an attempt")]
    public async Task Wait_signal_is_idempotent_while_pending()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        // Force a re-claim while still pending: the replayed wait must not duplicate the slot or consume an attempt.
        await SetJobStatusReadyAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var sig = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStateCode.Pending, sig.State);
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    [Fact(DisplayName = "Raise sets the slot and releases a Suspended job to Ready, then it completes")]
    public async Task Raise_signal_releases_suspended_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.Equal(JobCheckpointStateCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().State);
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobSignalRaised, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobResumed, ct));

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    [Fact(DisplayName = "A signal raised before the wait is observed without suspending the job")]
    public async Task Signal_raised_before_wait_is_observed()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        // Raise before the handler ever runs: the job is Ready, so the slot is created Set and the status is unchanged.
        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Ready, raise.Status);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Done, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobSuspended, ct));
    }

    [Fact(DisplayName = "A typed signal round-trips its payload to the handler")]
    public async Task Typed_signal_round_trips_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var decision = new ReviewDecision(true, "looks good");
        var raise = await Jobs.RaiseSignalAsync(enqueued, "review", decision, ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(decision, await Jobs.GetResultAsync<ReviewDecision>(enqueued, ct));
    }

    [Fact(DisplayName = "A presence signal sets the slot with a null payload")]
    public async Task Presence_signal_has_null_payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct)).Action);

        var sig = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStateCode.Set, sig.State);
        Assert.Equal(0, sig.ValueFormatId);
        Assert.Null(sig.Value);
    }

    [Fact(DisplayName = "A duplicate raise is last-writer-wins")]
    public async Task Duplicate_raise_is_last_writer_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(false, "first"), ct: ct);
        await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "second"), ct: ct);

        Assert.Equal(JobCheckpointStateCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().State);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(new ReviewDecision(true, "second"), await Jobs.GetResultAsync<ReviewDecision>(enqueued, ct));
    }

    [Fact(DisplayName = "Raise sets the slot but does not reactivate a paused job")]
    public async Task Raise_signal_does_not_unpause_paused_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var pause = await Jobs.PauseAsync(enqueued, ct: ct);
        Assert.Equal(JobControlAction.Applied, pause.Action);
        Assert.Equal(JobStatusCode.Paused, pause.Status);

        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Applied, raise.Action);
        Assert.Equal(JobStatusCode.Paused, raise.Status);

        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(JobCheckpointStateCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().State);
    }

    [Fact(DisplayName = "Raise is rejected against a terminal job and writes no slot")]
    public async Task Raise_signal_does_not_reactivate_terminal_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        Assert.Equal(JobControlAction.Applied, (await Jobs.CancelAsync(enqueued, ct: ct)).Action);

        var raise = await Jobs.RaiseSignalAsync(enqueued, "go", ct: ct);
        Assert.Equal(JobControlAction.Rejected, raise.Action);
        Assert.Equal(JobStatusCode.Cancelled, raise.Status);
        Assert.Empty(await ReadSignalsAsync(enqueued.JobId, ct));
    }

    [Fact(DisplayName = "Raise returns NotFound for an unknown job")]
    public async Task Raise_signal_returns_not_found_for_unknown_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var raise = await Jobs.RaiseSignalAsync(JobLookup.ById(long.MaxValue), "go", ct: ct);
        Assert.Equal(JobControlAction.NotFound, raise.Action);
        Assert.Null(raise.Status);
    }

    // ---------- helpers ----------

    private static Task SetJobStatusReadyAsync(IDbSession db, long jobId, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
