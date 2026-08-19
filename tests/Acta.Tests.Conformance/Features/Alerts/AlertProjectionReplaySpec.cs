using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// At-least-once conformance for the <c>sys.alerts</c> generate phase: the projector commits each
/// event's alert write before it advances its cursor, so a crash mid-batch re-offers events it has
/// already projected. The subject is the one event sequence that separates a correct implementation
/// from the plausible wrong ones - failure, success, failure on a single job id - because only a
/// success replayed <em>after</em> a later failure can close an alert it never opened. A repeated
/// failure alone would not: it survives implementations that guard the raise and nothing else. A
/// recurring slot carries the sequence, since only a recurring job outlives its own success, and the
/// failures are orphaned attempts, the failure shape a recurring slot records with a reason code.
/// </summary>
[ConformanceSpec(
    "alerts-projection.replay",
    "A replayed alert batch neither inflates a count nor re-closes an alert",
    Area = "Alerts",
    Contract = "Re-projecting events the sys.alerts cursor never advanced past leaves every alert those events already moved untouched.",
    Arrange = "A recurring slot is orphaned, then fires successfully, then is orphaned again, with the projector cursor still at zero.",
    Act = "The projector consumes the whole batch, loses its cursor advance to a crash, and consumes the identical batch again.",
    Assert = "The single alert keeps the occurrence count and open state the first pass left it, unwritten by the replay."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertProjectionReplaySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // A recurring slot: one stable job id that survives its own success and can fail again after it,
    // which is the whole reason this spec drives a schedule instead of a one-shot probe.
    private const string JobName = "recurring-ping";

    // sys.alerts keeps its projection cursor under this name on its own slot's variable bag; losing
    // that one write is the crash this spec stages.
    private const string CursorVariableName = "alerts-cursor";

    // Claim with the lease already lapsed so the sys.recovery sweep reclaims the attempt with no
    // real-time wait, exactly as ReclaimStuckJobsSpec does. Never reaches JobsOptions, which rejects a
    // non-positive lease.
    private const int ExpiredLeaseTtlSeconds = -5;

    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A success closes the slot's open failure alert and a later failure re-opens the same row")]
    public async Task Failure_success_failure_closes_then_reopens_one_row()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // An orphaned attempt: the projector opens a FirstFailure alert on the slot.
        await OrphanOneAttemptAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);
        var opened = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, opened.Kind);
        Assert.Equal(slotId, opened.JobId);
        Assert.Equal(1, opened.OccurrenceCount);
        Assert.Null(opened.ResolvedAtUtc);

        // The next fire succeeds: the projector closes that alert and writes none of its own.
        await SucceedOneFireAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(opened.Id, resolved.Id);
        Assert.Equal(1, resolved.OccurrenceCount);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // A second orphan inside the same dedupe window: the SAME row re-opens and counts the repeat.
        await OrphanOneAttemptAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);
        var reopened = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(opened.Id, reopened.Id);
        Assert.Equal(2, reopened.OccurrenceCount);
        Assert.Null(reopened.ResolvedAtUtc);
    }

    [Fact(DisplayName = "Replaying the batch a crashed pass never checkpointed changes nothing it already projected")]
    public async Task Replayed_batch_neither_inflates_the_count_nor_recloses_the_reopened_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Fail, succeed, fail on one stable slot id with no projector pass in between, so all three
        // events sit above the cursor and one batch carries the whole sequence.
        await OrphanOneAttemptAsync(slotId, ct);
        await SucceedOneFireAsync(slotId, ct);
        await OrphanOneAttemptAsync(slotId, ct);

        var secondFailureEventId = (await ReadLatestEventAsync(slotId, EventCode.JobExecutionFinished, ct)).Id;

        // The pass that projects the whole batch: raise, resolve, raise. One alert row, counting both
        // failures, left open by the second one.
        await RunAlertsAsync(slotId, ct);
        var projected = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, projected.Kind);
        Assert.Equal(slotId, projected.JobId);
        Assert.Equal(2, projected.OccurrenceCount);
        Assert.Null(projected.ResolvedAtUtc);
        Assert.Equal(secondFailureEventId, projected.LastProjectedEventId);

        // The crash: the pass committed every alert write, then died before writing its cursor back.
        // Dropping the variable restores the cursor to exactly what that pass started from - zero - so
        // the next pass re-reads the identical batch.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));

        await RunAlertsAsync(slotId, ct);

        // The replay is a no-op on every axis. The count is pinned to the exact 2 the two failures
        // earned - not merely "fewer than 4" - and it is asserted first, because inflation is the
        // failure this spec exists to catch and only the count names it. The row the second failure
        // re-opened is still open, so the replayed success did not close what a newer event had
        // re-opened, and the unchanged version proves the replay wrote nothing at all rather than
        // writing the same values back.
        var afterReplay = await ReadAlertsAsync(NamespaceId, ct);
        var replayed = Assert.Single(afterReplay, a => a.Id == projected.Id);
        Assert.Equal(2, replayed.OccurrenceCount);
        Assert.Null(replayed.ResolvedAtUtc);
        Assert.Equal(secondFailureEventId, replayed.LastProjectedEventId);
        Assert.Equal(projected.Version, replayed.Version);

        // And that row is still the namespace's only alert: an inflated count would have crossed
        // AlertFailureThreshold and manufactured a ThresholdReached row that nothing real justifies.
        Assert.Equal(AlertKindCode.FirstFailure, Assert.Single(afterReplay).Kind);
    }

    // ---------- driving the slot ----------

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        // The recurring slot's deduplication_key is the definition's job name.
        var id = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    /// <summary>
    /// One orphaned attempt on the slot: claim it with a lease that is already in the past, then let
    /// the recovery sweep reclaim it. That writes the slot's alertable failure event (Orphaned,
    /// <c>JobLeaseExpired</c>, back to Ready) and leaves the slot alive for the next fire.
    /// </summary>
    private async Task OrphanOneAttemptAsync(long slotId, CancellationToken ct)
    {
        await MakeSlotClaimableAsync(slotId, ct);

        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, NamespaceId, ct);
        var claim = await Services
            .GetRequiredService<IExecutionStore>()
            .ClaimOneAsync(NamespaceId, workerId, ExpiredLeaseTtlSeconds, slotId, ct);
        Assert.Equal(slotId, Assert.Single(claim).JobId);

        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, NamespaceId, ct)).Reclaimed);
    }

    /// <summary>
    /// One successful fire of the slot. The reclaim above leaves it claimable immediately; no schedule
    /// is due, so the handler runs with an empty triggering set and the slot rolls over to Ready with
    /// its failure count reset.
    /// </summary>
    private async Task SucceedOneFireAsync(long slotId, CancellationToken ct)
    {
        var before = RecurringPingHandler.TriggersFor(TestNamespace).Count;
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(before + 1, RecurringPingHandler.TriggersFor(TestNamespace).Count);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal((short)0, slot.FailureCount);
    }

    // The harness parks seeded slots a day out; pull this one back so the by-id claim, which filters on
    // next_run_at_utc like every other claim, can take it.
    private async Task MakeSlotClaimableAsync(long slotId, CancellationToken ct)
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        await Db.From<JobRuntime>().Where(r => r.Id == slotId).UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = due }, ct);
    }

    // ---------- driving the projector ----------

    private Task<int> ForgetAlertsCursorAsync(long slotId, CancellationToken ct) =>
        Db.From<JobCheckpoint>()
            .Where(v => v.JobId == slotId && v.Kind == JobCheckpointKindCode.Variable && v.Name == CursorVariableName)
            .DeleteAsync(ct);

    private async Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct)
    {
        var alertsJob = new AlertsJob(
            Services.GetRequiredService<IAlertStore>(),
            Services.GetRequiredService<IActaClock>(),
            Services.GetRequiredService<IAlertChannelRegistry>(),
            Services.GetRequiredService<IAlertTransportRegistry>(),
            Services.GetRequiredService<IOptions<JobsOptions>>()
        );

        await alertsJob.Handle(BuildAlertsContext(cursorOwnerJobId), ct);
    }

    // A JobContext standing in for the sys.alerts slot: the projector reads ctx.NamespaceId / JobNamespace
    // and stores the cursor variable as a checkpoints row keyed by the supplied (real) job's id.
    private RuntimeJobContext BuildAlertsContext(long cursorOwnerJobId)
    {
        var slot = new ClaimedJob(
            JobId: cursorOwnerJobId,
            JobRef: Guid.Empty,
            NamespaceId: NamespaceId,
            DefinitionId: 1,
            TenantId: null,
            ExecutionNumber: 1,
            DeduplicationKey: null,
            CorrelationKey: null,
            ExclusiveKey: null,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            NextRunAtUtc: null,
            LeaseExpiresAtUtc: default,
            CreatedAtUtc: default,
            FailureCount: 0,
            Version: 0
        );

        return new RuntimeJobContext(
            slot,
            jobName: "sys.alerts",
            namespaceName: TestNamespace,
            namespaceId: NamespaceId,
            leaseTtlSeconds: 180,
            jobStore: Services.GetRequiredService<IJobStore>(),
            signalStore: Services.GetRequiredService<ISignalStore>(),
            alerts: Services.GetRequiredService<IAlertSink>(),
            executionStore: Services.GetRequiredService<IExecutionStore>(),
            serializers: Services.GetRequiredService<IJobPayloadSerializerRegistry>(),
            lockStore: Services.GetRequiredService<ILockStore>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }
}
