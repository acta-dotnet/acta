using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for the leaderless reschedule + durable-sleep primitives: a re-arm flips the Job back to
/// <c>Ready</c> with a forward-dated <c>NextRunAtUtc</c> without charging the failure budget; durable
/// sleep arms an idempotent timer checkpoint and is consumed by the replayed handler once due.
/// </summary>
[ConformanceSpec(
    "reschedule-sleep.rearm-and-timer",
    "Reschedule re-arms Ready and durable sleep arms an idempotent timer",
    Area = "Scheduling",
    Contract = "Reschedule re-arms to Ready without charging the budget and durable sleep arms one idempotent timer checkpoint consumed by the replayed handler once due.",
    Arrange = "Handlers that reschedule or durably sleep are registered.",
    Act = "The runtime runs each job before and after the timer instant, and invalid, duplicate and unknown control paths are exercised.",
    Assert = "Reschedule re-arms Ready with a forward-dated next run and no budget charge, and one idempotent timer is consumed once due."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ArmOrConsumeSleepTimerAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class RescheduleSleepSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // ---------- P0: Reschedule ----------

    [Fact(DisplayName = "Reschedule re-arms Ready with a forward-dated next_run, no budget charge and no result")]
    public async Task Reschedule_via_context_method_rearms_ready_with_forward_dated_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-reschedule-delay", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Equal(0, job.FailureCount);
        Assert.NotNull(job.NextRunAtUtc);
        Assert.InRange(job.NextRunAtUtc!.Value, DateTime.UtcNow.AddMinutes(8), DateTime.UtcNow.AddMinutes(12));

        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.before", ct));
        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));

        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Rescheduled, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobRescheduled, ct));

        // Acceptance invariant: a re-arm writes no result payload.
        var result = await Services.GetRequiredService<IJobStore>().GetJobResultAsync(enqueued.JobId, null, ct);
        Assert.Null(result);
    }

    [Fact(DisplayName = "Reschedule by direct throw re-arms Ready like the context method")]
    public async Task Reschedule_via_direct_throw_rearms_like_the_context_method()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-reschedule-throw", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Rescheduled, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobRescheduled, ct));
    }

    [Fact(DisplayName = "Reschedule to an absolute past instant is immediately reclaimable")]
    public async Task Reschedule_to_absolute_past_is_immediately_reclaimable()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-reschedule-until-past", JobPayload.None), ct);

        // The probe re-arms to a past instant on every attempt, so two consecutive ticks both claim it.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
    }

    // ---------- P0: Sleep ----------

    [Fact(DisplayName = "First sleep arms one Pending timer and suspends the handler")]
    public async Task First_sleep_arms_pending_timer_and_suspends_the_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-basic", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var timers = await ReadTimersAsync(enqueued.JobId, ct);
        var nap = Assert.Single(timers);
        Assert.Equal("nap", nap.Name);
        Assert.Equal(JobCheckpointStatusCode.Pending, nap.Status);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, job.Status);
        Assert.Null(job.LeasedByWorkerId);
        Assert.Equal(nap.DueAtUtc, job.NextRunAtUtc);

        Assert.Equal(0, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
        Assert.Equal(1, await CountFinishedWithStatusAsync(enqueued.JobId, ExecutionStatusCode.Suspended, ct));
        Assert.Equal(1, await CountEventsAsync(enqueued.JobId, JobEventCode.JobSuspended, ct));
    }

    [Fact(DisplayName = "Sleep rerun before due does not extend or duplicate the timer")]
    public async Task Sleep_rerun_before_due_does_not_extend_or_duplicate_the_timer()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-basic", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        var originalDue = (await ReadTimersAsync(enqueued.JobId, ct)).Single().DueAtUtc;

        // Force a re-claim while the timer is still pending in the future (next_run past, due untouched).
        await SetJobNextRunAsync(Db, enqueued.JobId, DateTime.UtcNow.AddMinutes(-1), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var timer = Assert.Single(await ReadTimersAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Pending, timer.Status);
        Assert.Equal(originalDue, timer.DueAtUtc);
    }

    [Fact(DisplayName = "Sleep rerun after due consumes the timer and the handler continues to Succeeded")]
    public async Task Sleep_rerun_after_due_consumes_the_timer_and_continues()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-basic", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        // Drive the timer due: make the Job claimable and move the stored due into the past.
        var past = DateTime.UtcNow.AddMinutes(-1);
        await SetJobNextRunAsync(Db, enqueued.JobId, past, ct);
        await SetTimerDueAsync(Db, enqueued.JobId, "nap", past, ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var timer = Assert.Single(await ReadTimersAsync(enqueued.JobId, ct));
        Assert.Equal(JobCheckpointStatusCode.Consumed, timer.Status);

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, job.Status);
        Assert.Null(job.NextRunAtUtc);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    [Fact(DisplayName = "Zero-delay sleep continues without arming a timer")]
    public async Task Zero_delay_sleep_continues_without_arming_a_timer()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-zero", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Empty(await ReadTimersAsync(enqueued.JobId, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        Assert.Equal(1, await CountVariableAsync(enqueued.JobId, "ran.after", ct));
    }

    // ---------- P1: hardening ----------

    [Fact(DisplayName = "Sleep validation rejects invalid names, reserved names and negative delay")]
    public async Task Sleep_validation_rejects_invalid_names_and_negative_delay()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-validation", JobPayload.None), ct);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));

        var result = (await Jobs.GetResultAsync<SleepValidationResult>(enqueued, ct))!;
        Assert.True(result.InvalidNameRejected);
        Assert.True(result.ReservedNameRejected);
        Assert.True(result.NegativeDelayRejected);
        Assert.Empty(await ReadTimersAsync(enqueued.JobId, ct));
    }

    [Fact(DisplayName = "A second distinct pending sleep is rejected and re-arms without touching the existing timer")]
    public async Task A_second_distinct_pending_sleep_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-sleep-reject", JobPayload.None), ct);

        // A distinct pending sleep already controls the Job before the handler runs.
        await InsertPendingTimerAsync(Db, enqueued.JobId, "already-pending", DateTime.UtcNow.AddMinutes(10), ct);

        // The duplicate-sleep rejection surfaces as an unhandled exception, so the one-shot retry budget
        // re-arms the Job to Ready (the existing timer is untouched) rather than failing on the first try.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));

        var timers = await ReadTimersAsync(enqueued.JobId, ct);
        var only = Assert.Single(timers);
        Assert.Equal("already-pending", only.Name);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    [Fact(DisplayName = "Unknown control exception is rethrown, not translated to a reschedule or suspend")]
    public async Task Unknown_control_exception_is_rethrown_not_translated_to_a_rearm()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-control-unknown", JobPayload.None), ct);

        // An unrecognized JobControlException propagates (the worker loop logs it; lease expiry reclaims
        // the row) rather than being silently translated into a reschedule / suspend.
        await Assert.ThrowsAsync<FakeControlException>(async () => await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Executing, job.Status);
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobRescheduled, ct));
        Assert.Equal(0, await CountEventsAsync(enqueued.JobId, JobEventCode.JobSuspended, ct));
    }

    // ---------- helpers ----------

    private async Task<IReadOnlyList<JobCheckpoint>> ReadTimersAsync(long jobId, CancellationToken ct)
    {
        return await Db.From<JobCheckpoint>().Where(t => t.JobId == jobId && t.Kind == JobCheckpointKindCode.Timer).ToListAsync(ct);
    }

    private static Task SetJobNextRunAsync(IDbSession db, long jobId, DateTime nextRun, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_next", nextRun),
            ("@p_id", jobId)
        );

    private static Task SetTimerDueAsync(IDbSession db, long jobId, string name, DateTime due, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.checkpoints SET due_at_utc = @p_due WHERE job_id = @p_id AND kind_code = 30 AND name = @p_name",
            ct,
            ("@p_due", due),
            ("@p_id", jobId),
            ("@p_name", name)
        );

    private static Task InsertPendingTimerAsync(IDbSession db, long jobId, string name, DateTime due, CancellationToken ct) =>
        db.ExecuteRawAsync(
            """
            INSERT INTO {schema}.checkpoints (job_id, kind_code, name, status_code, due_at_utc, created_at_utc, modified_at_utc, version)
            VALUES (@p_id, 30, @p_name, 10, @p_due, @p_now, @p_now, 0)
            """,
            ct,
            ("@p_id", jobId),
            ("@p_name", name),
            ("@p_due", due),
            ("@p_now", DateTime.UtcNow)
        );
}
