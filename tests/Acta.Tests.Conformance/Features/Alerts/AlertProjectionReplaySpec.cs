using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
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
/// failures are orphaned attempts because the recovery sweep produces one with no real-time wait and
/// without the handler having to run.
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

    [Fact(DisplayName = "The dedupe bucket derives from the event's own instant, so a replay in a later bucket lands on the same row")]
    public async Task Window_bucket_derives_from_the_event_not_from_the_projecting_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Two orphaned attempts whose failure events are then back-dated into a bucket hours closed.
        // A projection that is a pure function of the event stream must land the alert there; one that
        // floors the pass's own clock lands it in the current bucket instead - and a crash-replay in
        // the NEXT bucket then mints a second row and re-delivers everything. A success between the
        // orphans resets the failure count: the recovery sweep terminalizes at the probe's MaxAttempts
        // (2), so back-to-back orphans would end in FinalFailure instead of a counted repeat. The
        // success event rides through the back-dating too, harmlessly - resolution has no bucket.
        await OrphanOneAttemptAsync(slotId, ct);
        await SucceedOneFireAsync(slotId, ct);
        await OrphanOneAttemptAsync(slotId, ct);

        var eventInstant = DateTime.UtcNow.AddHours(-3);
        Assert.Equal(3, await BackdateFinishedEventsAsync(slotId, eventInstant, ct));
        var expectedWindowStart = AlertWindow.FloorStart(eventInstant, DedupeWindow);

        await RunAlertsPinnedAsync(slotId, ct);
        var projected = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, projected.Kind);
        Assert.Equal(2, projected.OccurrenceCount);
        Assert.Equal(expectedWindowStart, projected.DedupeWindowStartUtc);

        // The crash: every alert write committed, the cursor write lost. The replay re-floors the same
        // event instants, lands every raise on the row above, and the high-water guard holds - the
        // whole alert set must come through unwritten.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));
        await RunAlertsPinnedAsync(slotId, ct);

        var afterReplay = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(projected.Id, afterReplay.Id);
        Assert.Equal(2, afterReplay.OccurrenceCount);
        Assert.Equal(expectedWindowStart, afterReplay.DedupeWindowStartUtc);
        Assert.Equal(projected.Version, afterReplay.Version);
    }

    [Fact(
        DisplayName = "A crash between the crossing event's FirstFailure and ThresholdReached emits recovers one correctly-sourced escalation"
    )]
    public async Task Interrupted_threshold_crossing_recovers_single_sourced_not_inflated()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Three failures on one slot, all back-dated to one instant so they deterministically share a
        // bucket and count 1, 2, 3 on one FirstFailure row; the third is the true crossing event. The
        // interleaved successes keep the slot under the recovery sweep's MaxAttempts (2) terminal, and
        // their resolves do not break the count: each re-raise re-opens the SAME row and keeps
        // incrementing it, so the crossing arithmetic is unchanged.
        await OrphanOneAttemptAsync(slotId, ct);
        await SucceedOneFireAsync(slotId, ct);
        await OrphanOneAttemptAsync(slotId, ct);
        await SucceedOneFireAsync(slotId, ct);
        await OrphanOneAttemptAsync(slotId, ct);
        Assert.Equal(5, await BackdateFinishedEventsAsync(slotId, DateTime.UtcNow.AddHours(-3), ct));
        var crossingEventId = (await ReadLatestEventAsync(slotId, EventCode.JobExecutionFinished, ct)).Id;

        await RunAlertsPinnedAsync(slotId, ct);
        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Equal(3, Assert.Single(alerts, a => a.Kind == AlertKindCode.FirstFailure).OccurrenceCount);
        var threshold = Assert.Single(alerts, a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(1, threshold.OccurrenceCount);
        Assert.Equal(crossingEventId, threshold.LastProjectedEventId);

        // The staged crash: the pass emitted the crossing event's FirstFailure but died before its
        // ThresholdReached landed. Both emits above DID land, so deleting the ThresholdReached row and
        // the cursor restores exactly that state, and the replay must recover the escalation.
        Assert.Equal(1, await Db.From<JobAlert>().Where(a => a.Id == threshold.Id).DeleteAsync(ct));
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));
        await RunAlertsPinnedAsync(slotId, ct);

        // Only the crossing event re-fires: the two earlier events' held raises return the stored
        // count (already at the threshold) with a newer mark and stay silent. Count 1 is asserted
        // first because inflation is the failure this fact exists to catch: under a bare
        // count-equals-threshold condition all three replayed raises re-fired, the first one minted
        // the row blaming the wrong event, and the other two inflated it to 3.
        var recovered = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(1, recovered.OccurrenceCount);
        Assert.Equal(crossingEventId, recovered.LastProjectedEventId);

        // A second replay leaves the recovered row untouched: the crossing event's re-fire lands on
        // the row's own high-water guard now, and the unchanged version proves it wrote nothing.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));
        await RunAlertsPinnedAsync(slotId, ct);
        var afterSecondReplay = await ReadAlertsAsync(NamespaceId, ct);
        var settled = Assert.Single(afterSecondReplay, a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(recovered.Id, settled.Id);
        Assert.Equal(1, settled.OccurrenceCount);
        Assert.Equal(crossingEventId, settled.LastProjectedEventId);
        Assert.Equal(recovered.Version, settled.Version);

        // The FirstFailure row rode through both replays untouched as well.
        Assert.Equal(3, Assert.Single(afterSecondReplay, a => a.Kind == AlertKindCode.FirstFailure).OccurrenceCount);
    }

    // ---------- driving the slot ----------

    private Task<long> SlotIdAsync(CancellationToken ct) => AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, JobName, ct);

    /// <summary>
    /// One orphaned attempt on the slot: claim it with a lease that is already in the past, then let
    /// the recovery sweep reclaim it. That writes the slot's alertable failure event (Orphaned,
    /// <c>JobLeaseExpired</c>, back to Ready) and leaves the slot alive for the next fire.
    /// </summary>
    private async Task OrphanOneAttemptAsync(long slotId, CancellationToken ct)
    {
        await AlertTestOps.MakeSlotClaimableAsync(Services, slotId, ct);

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

    /// <summary>
    /// Re-stamps every execution-finished event of the slot to <paramref name="instant"/> (the suite's
    /// UpdateOnlyAsync back-dating pattern): the projector derives the dedupe bucket from these
    /// stamps, so parking them hours in the past pins which bucket a correct projection lands in -
    /// deterministically, where events written at "now" would be at the mercy of a boundary crossing
    /// mid-fact. Success events are re-stamped along with the failures, harmlessly: resolution carries
    /// no bucket.
    /// </summary>
    private Task<int> BackdateFinishedEventsAsync(long slotId, DateTime instant, CancellationToken ct) =>
        Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == EventCode.JobExecutionFinished)
            .UpdateOnlyAsync(() => new JobEvent { CreatedAtUtc = instant }, ct);

    // ---------- driving the projector ----------

    // The compiled AlertsJob constant names the cursor variable, so the crash this spec stages keeps
    // targeting the projector's real checkpoint even if the projector renames it.
    private Task<int> ForgetAlertsCursorAsync(long slotId, CancellationToken ct) =>
        Db.From<JobCheckpoint>()
            .Where(v => v.JobId == slotId && v.Kind == JobCheckpointKindCode.Variable && v.Name == AlertsJob.CursorVariableName)
            .DeleteAsync(ct);

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, ct);

    // The crash-staging facts pin the window and threshold instead of inheriting the container's,
    // because their back-dated instants and expected counts are chosen against exactly these values -
    // and both passes of a staged crash must floor with the same window to mean anything.
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(1);

    private Task RunAlertsPinnedAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(
            Services,
            TestNamespace,
            NamespaceId,
            cursorOwnerJobId,
            new JobsOptions { AlertDedupeWindow = DedupeWindow, AlertFailureThreshold = 3 },
            ct
        );
}
