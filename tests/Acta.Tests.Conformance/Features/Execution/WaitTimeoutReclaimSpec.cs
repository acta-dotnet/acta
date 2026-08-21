using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// Conformance for the one attempt <c>reclaim_stuck_jobs</c> must not charge: the attempt that woke on a
/// wait deadline, flipped the awaited slot to Expired, and then lost its worker. The surviving path ends
/// that attempt Cancelled with <c>job.wait-timed-out</c> and no budget spent, so a reclaim that charged
/// it would let a crash fail a job the deadline had already resolved for free. Reclaim recognizes the
/// case from state alone - a slot of the job's own wait kinds, Expired, whose stored expiration is the
/// instant the suspend cached on the runtime row - and hands the job back Suspended on that same past
/// deadline, uncharged, for the replay to resolve as the waiting overload defines it.
///
/// <para>Uncharged is not unwatched. Spending no budget means a worker dying on the same wait every
/// time would loop without ever reaching MaxAttempts, so the reclaim's own event is alertable and each
/// pass raises the job's incident - the operator channel is what bounds the loop instead.</para>
/// </summary>
[ConformanceSpec(
    "reclaim-wait-timeout.recovery",
    "Reclaiming a crashed timeout resolution costs the job no retry budget",
    Area = "Recovery",
    Contract = "An expired-lease attempt whose awaited wait had already expired returns to Suspended uncharged, and still raises the job's automatic incident.",
    Arrange = "A bounded wait is armed, moved past its deadline, and claimed with an already-expired lease so the attempt reads as a dead worker.",
    Act = "The awaited slot is resolved through the wait routine or left Pending, the sweep reclaims the dead attempt, and the alerts projector passes over it.",
    Assert = "The uncharged re-arm opens an incident a later success resolves, while a Pending or already-passed wait is charged as an ordinary lost lease."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class WaitTimeoutReclaimSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Claim with a negative TTL so the lease lands in the past: the worker that took this attempt is
    // dead before it can do anything else, with no wall-clock wait and no fault injection.
    private const int DeadLeaseTtlSeconds = -5;

    [Fact(DisplayName = "A crash after the wait expired re-arms the job Suspended on the same deadline with no attempt charged")]
    public async Task Crash_after_the_wait_expired_is_reclaimed_without_charging_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);
        var deadline = (await ReadJobAsync(enqueued.JobId, ct)).NextRunAtUtc;

        // The claim the deadline earned, and then the last thing the lost worker durably did: the same
        // routine its replayed handler calls, flipping the overdue slot to Expired. Everything after
        // that - the throw, the completion - died with the process.
        var claimed = Assert.Single(
            await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, DeadLeaseTtlSeconds, enqueued, ct)
        );
        Assert.Equal(enqueued.JobId, claimed.JobId);
        var resolved = await Services
            .GetRequiredService<ISignalStore>()
            .WaitSignalAsync(enqueued.JobId, JobCheckpointKindCode.Signal, "go", timeoutSeconds: null, ct);
        Assert.Equal(SignalWaitOutcomeCode.TimedOut, resolved.Outcome);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        // Back where the deadline left it, minus a worker: Suspended on the same instant, unleased, and
        // owing nothing. Retention stays clear because nothing terminal happened.
        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Suspended, reclaimed.Status);
        Assert.Equal(0, reclaimed.FailureCount);
        Assert.Equal(deadline, reclaimed.NextRunAtUtc);
        Assert.Null(reclaimed.LeasedByWorkerId);
        Assert.Null(reclaimed.LeaseExpiresAtUtc);
        Assert.Null(reclaimed.RetentionUntilUtc);

        var recovery = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(ExecutionStatusCode.Orphaned, recovery.ExecutionStatus);
        Assert.Equal(JobStatusCode.Suspended, recovery.ToStatus);
        Assert.Equal(JobEventReasonCode.JobLeaseExpired, recovery.ReasonCode);

        // The replay lands exactly what the surviving path would have landed, still budget-neutral.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var settled = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Cancelled, settled.Status);
        Assert.Equal(0, settled.FailureCount);
        Assert.NotNull(settled.RetentionUntilUtc);
        Assert.Equal(
            JobEventReasonCode.JobWaitTimedOut,
            (await ReadLatestEventAsync(enqueued.JobId, EventCode.JobCancelled, ct)).ReasonCode
        );
    }

    [Fact(DisplayName = "A crash before the wait expired is charged and re-armed the ordinary way")]
    public async Task Crash_before_the_wait_expired_is_charged_like_any_lost_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The worker died before its replay reached the wait, so the slot never flipped. Nothing has
        // resolved, the replay still has its whole timeout to resolve, and the lost lease is charged.
        Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, DeadLeaseTtlSeconds, enqueued, ct));
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Equal(1, reclaimed.FailureCount);
        Assert.Equal(JobCheckpointStatusCode.Pending, (await ReadSignalsAsync(enqueued.JobId, ct)).Single().Status);
    }

    [Fact(DisplayName = "An expired slot the handler already passed does not make a later crash free")]
    public async Task An_expired_slot_the_handler_passed_does_not_excuse_a_later_crash()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout-then-hold", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        // The Try wait resolves TimedOut and the handler parks on a second, unbounded wait: the job now
        // carries an Expired slot it walked past, which must not read as a deadline still resolving.
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobCheckpointStatusCode.Expired, (await ReadSignalsAsync(enqueued.JobId, ct)).Single(s => s.Name == "go").Status);

        Assert.Equal(ControlAction.Applied, (await Jobs.RaiseSignalAsync(enqueued, "hold", JobPayload.None, ct: ct)).Action);
        Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, DeadLeaseTtlSeconds, enqueued, ct));
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        // A released job, not a job waiting on a deadline: the lost lease is charged like any other.
        var reclaimed = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, reclaimed.Status);
        Assert.Equal(1, reclaimed.FailureCount);
    }

    [Fact(DisplayName = "The uncharged reclaim still opens an incident under the job's own alert profile")]
    public async Task The_uncharged_reclaim_is_still_visible_as_an_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-wait-signal-timeout", JobPayload.None), ct);
        await ReclaimAfterResolvedWaitAsync(enqueued, ns, ct);

        // Spending no budget is what makes this the only place the loop can surface: the reclaim's
        // event lands Suspended rather than Ready, so the projector has to admit that shape or a
        // worker crashing on the same wait forever is a silent one.
        await RunAlertsAsync(enqueued.JobId, ns, ct);
        var raised = Assert.Single(await ReadAlertsAsync(ns, ct));
        Assert.Equal(enqueued.JobId, raised.JobId);
        Assert.Equal(AlertOriginCode.Automatic, raised.OriginCode);
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Null(raised.ResolvedAtUtc);

        // The replay gives up on the wait, which is a Cancelled landing: it alerts under no profile and
        // resolves nothing, so the incident the crash opened stays open for someone to look at.
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(enqueued.JobId, ct)).Status);
        await RunAlertsAsync(enqueued.JobId, ns, ct);

        var afterCancel = Assert.Single(await ReadAlertsAsync(ns, ct));
        Assert.Equal(AlertKindCode.FirstFailure, afterCancel.Kind);
        Assert.Null(afterCancel.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A success after an uncharged reclaim resolves the incident it opened")]
    public async Task A_success_after_the_uncharged_reclaim_resolves_the_incident()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];

        // The Try overload resumes past its expired wait, so this job is the one-off: one worker died
        // on the timeout resolution, the replay finished the work, and nothing is wrong any more.
        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "job-try-wait-signal-timeout", JobPayload.None), ct);
        await ReclaimAfterResolvedWaitAsync(enqueued, ns, ct);

        await RunAlertsAsync(enqueued.JobId, ns, ct);
        Assert.Null(Assert.Single(await ReadAlertsAsync(ns, ct)).ResolvedAtUtc);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(enqueued, ct));
        var settled = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Succeeded, settled.Status);
        Assert.Equal(0, settled.FailureCount);

        // The ordinary lifecycle closes it: the success is the recovery, so the incident the crash
        // opened is resolved rather than left for an operator to clear by hand.
        await RunAlertsAsync(enqueued.JobId, ns, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(ns, ct));
        Assert.Equal(AlertKindCode.FirstFailure, resolved.Kind);
        Assert.NotNull(resolved.ResolvedAtUtc);
    }

    // The whole crash-after-the-flip staging, shared by the facts that go on to ask what it looked
    // like from outside: arm the wait, move it past its deadline, claim it with a lease that is
    // already dead, make the flip the lost worker made, and sweep.
    private async Task ReclaimAfterResolvedWaitAsync(JobEnqueueOutcome enqueued, short ns, CancellationToken ct)
    {
        var worker = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        Assert.Equal(RunOnceOutcome.Rearmed, await Runtime.RunOnceAsync(enqueued, ct));
        await ExpireWaitAsync(Db, enqueued.JobId, "go", ct);

        Assert.Single(await Services.GetRequiredService<IExecutionStore>().ClaimOneAsync(ns, worker, DeadLeaseTtlSeconds, enqueued, ct));
        var resolved = await Services
            .GetRequiredService<ISignalStore>()
            .WaitSignalAsync(enqueued.JobId, JobCheckpointKindCode.Signal, "go", timeoutSeconds: null, ct);
        Assert.Equal(SignalWaitOutcomeCode.TimedOut, resolved.Outcome);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(enqueued.JobId, ct)).Status);
    }

    private Task RunAlertsAsync(long jobId, short ns, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, ns, jobId, options: null, drain: null, ct);

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
}
