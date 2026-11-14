using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable signal-wait timeouts: the expiration lives on the awaited checkpoint,
/// the Suspended Job carries it in <c>NextRunAtUtc</c> so the claim wakes at the deadline, and
/// <c>wait_job_signal</c>'s checkpoint-locked re-entry is the sole arbiter that flips an overdue Pending checkpoint to
/// <c>Expired</c>. Completion before the deadline wins; a raise after it revives nothing.
/// </summary>
[ConformanceSpec(
    "signals.wait-timeout",
    "A bounded wait expires on its checkpoint's stored instant, once and for good",
    Area = "Signals",
    Contract = "A bounded wait stores an absolute expiration on its checkpoint, wakes the Suspended job at that instant, resolves TimedOut once, and revives on no later raise."
)]
[Deploys(
    "job-wait-signal-outcome",
    "job-wait-signal-outcome-then-hold",
    "job-wait-signal-outcome-then-unbounded",
    "job-wait-signal-outcome-typed",
    "job-wait-signal",
    "job-wait-signal-timeout",
    "job-wait-signal-timeout-downgrade",
    "job-wait-signal-timeout-replay",
    "job-wait-signal-timeout-typed",
    "job-wait-signal-timeout-upgrade"
)]
public sealed class SignalTimeoutSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<SignalTimeoutSpec>>
{
    [Fact(DisplayName = "Arming stores an expiration on the checkpoint and carries it into the Suspended job's next run")]
    public async Task Arming_stores_the_expiration_on_the_checkpoint_and_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal("go", checkpoint.CheckpointName);
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.NotNull(checkpoint.DueAtUtc);

        // The deadline is written once, on the checkpoint; the jobs_state row only caches it for the claim.
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Equal(checkpoint.DueAtUtc, job.NextRunAtUtc);
        Assert.Null(job.LeasedByWorkerId);
    }

    [Fact(DisplayName = "A signal before the deadline delivers its typed payload and leaves no timeout artifacts")]
    public async Task Signal_before_the_deadline_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(
            ControlAction.Applied,
            (await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "in time"), ct: ct)).Action
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var report = await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct);
        Assert.Equal(new WaitTimeoutReport(TimedOut: false, Received: true, "in time"), report);

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Set, checkpoint.Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountReasonAsync(enqueued.JobId, EventReason.WaitTimedOut, ct));
    }

    [Fact(DisplayName = "A presence-only signal before the deadline resumes the handler")]
    public async Task Presence_signal_before_the_deadline_resumes_the_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "A timeout the handler throws with ThrowIfTimedOut fails the job at once, with no budget charge")]
    public async Task Timeout_thrown_by_the_handler_fails_the_job_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The claim admits the due Suspended row; the replayed wait answers TimedOut, and the handler throws it. A retry
        // would meet the same expired wait, so the default budget's attempts are not spent on it.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));

        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Contains("The wait for signal 'go' timed out.", failed.EventMessage ?? "", StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An expired bounded wait resumes the handler exactly once with a TimedOut outcome")]
    public async Task Expired_bounded_wait_resumes_the_handler_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: true, Received: false, null),
            await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct)
        );
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);

        // Notes append rather than upsert, so one note is the whole "the code after the wait ran once" fact.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobNoteRecorded, ct));
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    [Fact(DisplayName = "The typed outcome carries the payload before the deadline and a null value after it")]
    public async Task Typed_outcome_covers_both_cases()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(received, ct));
        await Jobs.RaiseSignalAsync(received, "review", new ReviewDecision(true, "looks good"), ct: ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(received, ct));
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: false, Received: true, "looks good"),
            await Jobs.GetResultAsync<WaitTimeoutReport>(received, ct)
        );

        var expired = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(expired, ct));
        await ExpireWaitAsync(Db, expired.JobId, "review", ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(expired, ct));
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: true, Received: false, null),
            await Jobs.GetResultAsync<WaitTimeoutReport>(expired, ct)
        );
    }

    [Fact(DisplayName = "A replay asking for a longer wait does not move the stored expiration")]
    public async Task Replay_does_not_extend_the_stored_expiration()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout-replay", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);

        // Force a re-claim while the wait is still pending. The replayed handler asks for twice the
        // original timeout; the existing checkpoint must win over the code that re-entered it.
        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.Equal(armed, checkpoint.DueAtUtc);
        Assert.Equal(armed, (await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);
    }

    [Fact(DisplayName = "A raise after the deadline but before the wait re-enters still wins")]
    public async Task Completion_wins_the_race_against_an_overdue_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // Deterministic staging of the race the checkpoint lock arbitrates: the checkpoint is already overdue when
        // the raise lands, and the wait has not re-entered yet. The wait had not resolved, so the raise
        // is not late and completion must win.
        await ExpireWaitAsync(Db, enqueued.JobId, "review", ct);
        Assert.Equal(
            ControlAction.Applied,
            (await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "just in time"), ct: ct)).Action
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(
            new WaitTimeoutReport(TimedOut: false, Received: true, "just in time"),
            await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct)
        );
        Assert.Equal(CheckpointStatusCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
        Assert.Equal(0, await CountReasonAsync(enqueued.JobId, EventReason.WaitTimedOut, ct));
    }

    [Fact(DisplayName = "A raise on an expired checkpoint is rejected, writes nothing, and the replayed wait stays TimedOut")]
    public async Task Late_raise_cannot_revive_an_expired_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-then-hold", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The wait resolves TimedOut and the handler parks on a second, unbounded signal, so the job is
        // alive and Suspended while "go" is already Expired.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var parked = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, parked.Status);
        Assert.Null(parked.NextRunAtUtc);

        var late = await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct);
        Assert.Equal(ControlAction.Rejected, late.Action);
        Assert.Equal(JobStatusCode.Suspended, late.Status);

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, after.Status);
        Assert.Null(after.NextRunAtUtc);
        Assert.Equal(parked.Version, after.Version);
        var go = (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.CheckpointName == "go");
        Assert.Equal(CheckpointStatusCode.Expired, go.Status);
        Assert.Equal(0, go.ValueFormatId);

        // A rejected raise writes nothing, its event included: the answer itself says the raise changed nothing.
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobSignalRaised, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobResumed, ct));

        // Releasing the second signal replays the handler over the expired checkpoint: it still resolves
        // TimedOut, deterministically, and the job runs to completion.
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "hold", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(
            CheckpointStatusCode.Expired,
            (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.CheckpointName == "go").Status
        );
    }

    [Fact(DisplayName = "An unbounded wait replayed over an expired checkpoint fails the job with the timeout instead of parking")]
    public async Task Unbounded_replay_over_an_expired_checkpoint_fails_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-then-unbounded", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The bounded wait answers TimedOut and parks the handler on a second, unbounded signal, so
        // the job is alive while "go" is already Expired.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);

        // Releasing the park replays the handler over the same checkpoint with the unbounded overload. State
        // records only the expiration, and an unbounded wait has no outcome to report it in, so it fails the job
        // with the timeout rather than parking forever.
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "hold", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.Equal(
            EventReason.NonRetryableException,
            EventDetail(await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct), "reason")
        );

        var go = (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.CheckpointName == "go");
        Assert.Equal(CheckpointStatusCode.Expired, go.Status);
        Assert.Equal(0, go.ValueFormatId);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobNoteRecorded, ct));
    }

    [Fact(DisplayName = "A replay carrying a bound arms the deadline an unbounded wait never had")]
    public async Task Replay_upgrades_an_unbounded_wait_to_a_bounded_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout-upgrade", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Null((await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc);
        Assert.Null((await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);

        // The stranded job is unclaimable by construction, so an operator reschedule is what gets the
        // replay running; the arming then happens on that claimed attempt.
        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);
        // The same attempt that armed the checkpoint completes through the suspend branch, so the wake time
        // is cached on the jobs_state row without needing a second tick.
        var suspended = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, suspended.Status);
        Assert.Equal(armed, suspended.NextRunAtUtc);

        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "A replay dropping the bound does not clear the deadline the checkpoint carries")]
    public async Task Replay_without_a_bound_does_not_clear_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout-downgrade", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);

        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.Equal(armed, checkpoint.DueAtUtc);
        Assert.Equal(armed, (await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);
    }

    [Fact(DisplayName = "A bounded wait arriving at a checkpoint an unbounded one already armed still arms the deadline")]
    public async Task Bounded_wait_arms_a_checkpoint_an_unbounded_arrival_already_created()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);

        // The unbounded arrival's checkpoint, armed by the handler itself. Two first arrivals racing the same
        // absent checkpoint leave exactly this state for the loser, which is the interleaving this pins: the
        // caller that carries a bound must arm the deadline whether it created the row or found it.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Null((await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc);

        // The bounded arrival is the next execution's, so the job stands executing it.
        var next = await ExecutionFenceStaging.ExecutingAsync(Db, enqueued.JobId, ct, executionNumber: 2);
        var decision = await Services
            .GetRequiredService<SignalsOperations>()
            .WaitJobSignalAsync(next, CheckpointKindCode.Signal, "go", timeoutSeconds: 900, dueAtUtc: null, ct);

        // Still pending - the bound arms a deadline, it does not resolve the wait - and now bounded, so
        // the job the next suspend caches it on can be claimed back at that instant.
        Assert.Equal(WaitJobSignalOutcomeCode.SuspendPending, decision.Outcome);
        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.NotNull(checkpoint.DueAtUtc);
    }

    [Fact(DisplayName = "An unbounded wait still suspends with no due instant and stays unclaimable")]
    public async Task Unbounded_wait_keeps_its_old_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var checkpoint = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(CheckpointStatusCode.Pending, checkpoint.Status);
        Assert.Null(checkpoint.DueAtUtc);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Null(job.NextRunAtUtc);

        // The claim widened to Suspended rows, but a NULL next run is claimable for Ready only: an
        // unbounded wait must stay parked until a raise releases it.
        Assert.Equal(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A restart clears the expired wait that ended the job, which waits again under a fresh bound")]
    public async Task Restart_reopens_the_wait_that_ended_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(enqueued.JobId, ct)).Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);
        Assert.DoesNotContain(await ReadSignalsAsync(enqueued.JobId, ct), s => s.CheckpointName == "go");

        // The replay arms the wait again, a raise now counts, and the job runs on to its end.
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var go = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct), s => s.CheckpointName == "go");
        Assert.Equal(CheckpointStatusCode.Pending, go.Status);
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "A restart keeps the expiry of a timed-out wait the handler went past")]
    public async Task Restart_keeps_an_expiry_the_handler_went_past()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-wait-signal-outcome-then-hold", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(enqueued, ct: ct)).Action);

        // The handler wrote what came after the timeout and parked on "hold", so the timeout did not end the job.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);
        var go = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct), s => s.CheckpointName == "go");
        Assert.Equal(CheckpointStatusCode.Expired, go.Status);
    }

    // Moves the wait past its deadline the way real time would: the checkpoint's stored expiration and the
    // job's cached claim instant both go into the past, and nothing else is touched.
    private static async Task ExpireWaitAsync(IDbSession db, long jobId, string name, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        await db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due"
                + " WHERE job_id = @p_id AND checkpoint_kind_code = 20 /* CheckpointKindCode.Signal */ AND checkpoint_name = @p_name",
            ct,
            ("@p_due", past),
            ("@p_id", jobId),
            ("@p_name", name)
        );
        await db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", past),
            ("@p_id", jobId)
        );
    }

    private static Task ForceClaimableAsync(IDbSession db, long jobId, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
