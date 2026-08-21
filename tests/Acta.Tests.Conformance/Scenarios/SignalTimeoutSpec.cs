using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for durable signal-wait timeouts: the expiration lives on the awaited checkpoint slot,
/// the Suspended Job carries it in <c>NextRunAtUtc</c> so the claim wakes at the deadline, and
/// <c>wait_signal</c>'s slot-locked re-entry is the sole arbiter that flips an overdue Pending slot to
/// <c>Expired</c>. Completion before the deadline wins; a raise after it revives nothing.
/// </summary>
[ConformanceSpec(
    "signals.wait-timeout",
    "A bounded wait expires on its slot's stored instant, once and for good",
    Area = "Signals",
    Contract = "A bounded wait stores an absolute expiration on its slot, wakes the Suspended job at that instant, resolves TimedOut once, and revives on no later raise.",
    Arrange = "Handlers waiting with a 30-minute timeout are registered so only a deliberate rewind of the stored expiration can expire a wait.",
    Act = "The runtime ticks each job before and after the persisted expiration is moved into the past, with raises landing before, during and after the deadline.",
    Assert = "An expired wait cancels budget-neutrally or resumes TimedOut, replay never extends the stored instant, and a late raise leaves the slot Expired."
)]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.WaitSignalAsync))]
[CoversStoreMethod(typeof(ISignalStore), nameof(ISignalStore.RaiseSignalAsync))]
public abstract class SignalTimeoutSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "Arming stores an expiration on the slot and carries it into the Suspended job's next run")]
    public async Task Arming_stores_the_expiration_on_the_slot_and_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var slot = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal("go", slot.Name);
        Assert.Equal(JobCheckpointStatusCode.Pending, slot.Status);
        Assert.NotNull(slot.DueAtUtc);

        // The deadline is written once, on the slot; the runtime row only caches it for the claim.
        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Equal(slot.DueAtUtc, job.NextRunAtUtc);
        Assert.Null(job.LeasedByWorkerId);
    }

    [Fact(DisplayName = "A signal before the deadline delivers its typed payload and leaves no timeout artifacts")]
    public async Task Signal_before_the_deadline_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout-typed", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(
            ControlAction.Applied,
            (await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "in time"), ct: ct)).Action
        );
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var report = await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct);
        Assert.Equal(new WaitTimeoutReport(TimedOut: false, Received: true, "in time"), report);

        var slot = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Set, slot.Status);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(0, await CountReasonAsync(enqueued.JobId, JobEventReasonCode.JobWaitTimedOut, ct));
    }

    [Fact(DisplayName = "A presence-only signal before the deadline resumes the non-Try overload")]
    public async Task Presence_signal_before_the_deadline_resumes_the_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "An expired non-Try wait cancels the job with job.wait-timed-out and no budget charge")]
    public async Task Expired_non_try_wait_cancels_the_job_budget_neutrally()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var before = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(0, before.FailureCount);
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The claim admits the due Suspended row; the replayed wait is what actually resolves it.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.Null(job.LeasedByWorkerId);

        Assert.Equal(JobCheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Cancelled, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobCancelled, ct));
        Assert.Equal(2, await CountReasonAsync(enqueued.JobId, JobEventReasonCode.JobWaitTimedOut, ct));
    }

    [Fact(DisplayName = "Claiming a due Suspended row records Suspended as the started event's from-status")]
    public async Task Claim_records_the_real_prior_status_on_the_started_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The combined claim is the poll loop's shape and the only one that writes the started event
        // itself; RunOnceAsync claims to Dispatched, so start_execution owns the event there instead.
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == ns).SingleOrDefaultAsync(ct);
        Assert.NotNull(worker);
        var leaseTtl = Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseTtlSeconds;

        var claimed = await Services
            .GetRequiredService<IExecutionStore>()
            .ClaimBatchAsync(new ClaimRequest(ns, worker!.Id, MaxBatch: 8, StartExecuting: true), leaseTtl, ct);
        Assert.Contains(claimed.Jobs, j => j.JobId == enqueued.JobId);

        // The row came from Suspended, so the timeline says Suspended. A hard-coded Ready would assert
        // a transition that never happened, which is the whole reason the claim carries the old status.
        var started = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionStarted, ct);
        Assert.Equal(JobStatusCode.Suspended, started.FromStatus);
        Assert.Equal(JobStatusCode.Executing, started.ToStatus);
    }

    [Fact(DisplayName = "An expired Try wait resumes the handler exactly once with a TimedOut result")]
    public async Task Expired_try_wait_resumes_the_handler_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: true, Received: false, null),
            await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct)
        );
        Assert.Equal(JobCheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);

        // Notes append rather than upsert, so one note is the whole "the code after the wait ran once" fact.
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, EventCode.JobNoteRecorded, ct));
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    [Fact(DisplayName = "The typed Try overload carries the payload before the deadline and a null value after it")]
    public async Task Typed_try_overload_covers_both_outcomes()
    {
        var ct = TestContext.Current.CancellationToken;
        var received = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-typed", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(received, ct));
        await Jobs.RaiseSignalAsync(received, "review", new ReviewDecision(true, "looks good"), ct: ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(received, ct));
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: false, Received: true, "looks good"),
            await Jobs.GetResultAsync<WaitTimeoutReport>(received, ct)
        );

        var expired = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-typed", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(expired, ct));
        await ExpireWaitAsync(Db, expired.JobId, "review", ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(expired, ct));
        Assert.Equal(
            new WaitTimeoutReport(TimedOut: true, Received: false, null),
            await Jobs.GetResultAsync<WaitTimeoutReport>(expired, ct)
        );
    }

    [Fact(DisplayName = "A replay asking for a longer wait does not move the stored expiration")]
    public async Task Replay_does_not_extend_the_stored_expiration()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout-replay", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);

        // Force a re-claim while the wait is still pending. The replayed handler asks for twice the
        // original timeout; the existing slot must win over the code that re-entered it.
        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var slot = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Pending, slot.Status);
        Assert.Equal(armed, slot.DueAtUtc);
        Assert.Equal(armed, (await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);
    }

    [Fact(DisplayName = "A raise after the deadline but before the wait re-enters still wins")]
    public async Task Completion_wins_the_race_against_an_overdue_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-typed", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // Deterministic staging of the race the slot lock arbitrates: the slot is already overdue when
        // the raise lands, and the wait has not re-entered yet. The wait had not resolved, so the raise
        // is not late and completion must win.
        await ExpireWaitAsync(Db, enqueued.JobId, "review", ct);
        Assert.Equal(
            ControlAction.Applied,
            (await Jobs.RaiseSignalAsync(enqueued, "review", new ReviewDecision(true, "just in time"), ct: ct)).Action
        );

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(
            new WaitTimeoutReport(TimedOut: false, Received: true, "just in time"),
            await Jobs.GetResultAsync<WaitTimeoutReport>(enqueued, ct)
        );
        Assert.Equal(JobCheckpointStatusCode.Set, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
        Assert.Equal(0, await CountReasonAsync(enqueued.JobId, JobEventReasonCode.JobWaitTimedOut, ct));
    }

    [Fact(DisplayName = "A raise on an expired slot revives nothing and the replayed wait stays TimedOut")]
    public async Task Late_raise_cannot_revive_an_expired_wait()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-then-hold", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The wait resolves TimedOut and the handler parks on a second, unbounded signal, so the job is
        // alive and Suspended while "go" is already Expired.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var parked = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, parked.Status);
        Assert.Null(parked.NextRunAtUtc);

        var late = await Jobs.RaiseSignalAsync(enqueued, "go", JobPayload.None, ct: ct);
        Assert.Equal(ControlAction.Applied, late.Action);
        Assert.Equal(JobStatusCode.Suspended, late.Status);

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, after.Status);
        Assert.Null(after.NextRunAtUtc);
        Assert.Equal(parked.Version, after.Version);
        var go = (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.Name == "go");
        Assert.Equal(JobCheckpointStatusCode.Expired, go.Status);
        Assert.Equal(0, go.ValueFormatId);

        // The raise changed nothing, but it happened: the timeline says so and says why, or an operator
        // is left with a verb that reported Applied and a job that never moved.
        var raised = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobSignalRaised, ct);
        Assert.Contains("already expired", raised.ReasonMessage ?? "", StringComparison.Ordinal);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobResumed, ct));

        // Releasing the second signal replays the handler over the expired slot: it still resolves
        // TimedOut, deterministically, and the job runs to completion.
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "hold", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(JobCheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.Name == "go").Status);
    }

    [Fact(DisplayName = "An unbounded wait replayed over an expired slot cancels the job instead of parking")]
    public async Task Unbounded_replay_over_an_expired_slot_cancels_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-then-unbounded", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The Try overload resolves TimedOut and parks the handler on a second, unbounded signal, so
        // the job is alive while "go" is already Expired.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);

        // Releasing the park replays the handler over the same slot with the unbounded overload. State
        // records only the expiration; the code that re-enters decides what reaching it means, and the
        // unbounded overload ends the job rather than returning a result.
        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "hold", JobPayload.None, ct: ct)).Action);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.RetentionUntilUtc);
        Assert.Equal(
            JobEventReasonCode.JobWaitTimedOut,
            (await ReadLatestEventAsync(enqueued.JobId, EventCode.JobCancelled, ct)).ReasonCode
        );

        var go = (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.Name == "go");
        Assert.Equal(JobCheckpointStatusCode.Expired, go.Status);
        Assert.Equal(0, go.ValueFormatId);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, EventCode.JobNoteRecorded, ct));
    }

    [Fact(DisplayName = "A replay carrying a bound arms the deadline an unbounded wait never had")]
    public async Task Replay_upgrades_an_unbounded_wait_to_a_bounded_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout-upgrade", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Null((await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc);
        Assert.Null((await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);

        // The stranded job is unclaimable by construction, so an operator reschedule is what gets the
        // replay running; the arming then happens on that claimed attempt.
        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);
        // The same attempt that armed the slot completes through the suspend branch, so the wake time
        // is cached on the runtime row without needing a second tick.
        var suspended = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, suspended.Status);
        Assert.Equal(armed, suspended.NextRunAtUtc);

        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(JobCheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "A replay dropping the bound does not clear the deadline the slot carries")]
    public async Task Replay_without_a_bound_does_not_clear_the_deadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout-downgrade", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var armed = (await ReadSignalsAsync(enqueued.JobId, ct)).Single().DueAtUtc;
        Assert.NotNull(armed);

        await ForceClaimableAsync(Db, enqueued.JobId, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var slot = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Pending, slot.Status);
        Assert.Equal(armed, slot.DueAtUtc);
        Assert.Equal(armed, (await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc);
    }

    [Fact(DisplayName = "An unbounded wait still suspends with no due instant and stays unclaimable")]
    public async Task Unbounded_wait_keeps_its_old_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var slot = Assert.Single(await ReadSignalsAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Pending, slot.Status);
        Assert.Null(slot.DueAtUtc);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, job.Status);
        Assert.Null(job.NextRunAtUtc);

        // The claim widened to Suspended rows, but a NULL next run is claimable for Ready only: an
        // unbounded wait must stay parked until a raise releases it.
        Assert.Equal(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    // ---------- helpers ----------

    private async Task<int> CountReasonAsync(long jobId, JobEventReasonCode reason, CancellationToken ct) =>
        await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.ReasonCode == reason).CountAsync(ct);

    // Moves the wait past its deadline the way real time would: the slot's stored expiration and the
    // job's cached claim instant both go into the past, and nothing else is touched.
    private static async Task ExpireWaitAsync(IDbSession db, long jobId, string name, CancellationToken ct)
    {
        var past = DateTime.UtcNow.AddMinutes(-1);
        await db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due WHERE job_id = @p_id AND kind_code = 20 AND name = @p_name",
            ct,
            ("@p_due", past),
            ("@p_id", jobId),
            ("@p_name", name)
        );
        await db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", past),
            ("@p_id", jobId)
        );
    }

    private static Task ForceClaimableAsync(IDbSession db, long jobId, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
