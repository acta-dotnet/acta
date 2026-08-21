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
/// At-least-once conformance for the <c>sys.alerts</c> generate phase under incident identity: the
/// projector commits each event's alert write before it advances its cursor, so a crash mid-batch
/// re-offers events it has already projected. The subject is the one event sequence that separates a
/// correct implementation from the plausible wrong ones - failure, success, failure on a single job id -
/// because a success is terminal for the incident it closes, and the failure behind it must neither
/// inflate the incident it already moved nor resurrect the one that closed. A recurring slot carries the
/// sequence, since only a recurring job outlives its own success, and the failures are orphaned attempts
/// because the recovery sweep produces one with no real-time wait and without the handler having to run.
/// </summary>
[ConformanceSpec(
    "alerts-projection.replay",
    "A replayed alert batch neither inflates an incident nor opens a ghost one",
    Area = "Alerts",
    Contract = "Re-projecting events the sys.alerts cursor never advanced past leaves the incidents they moved untouched and opens none behind a resolution.",
    Arrange = "A recurring slot is orphaned, then fires successfully, then is orphaned again, with the projector cursor still at zero.",
    Act = "The projector consumes the whole batch, loses its cursor advance to a crash, and consumes the identical batch again.",
    Assert = "The two incident rows keep the counts, refs, and open/resolved state the first pass left them, unwritten by the replay."
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

    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A flap is a new incident: the success closes the row for good and the next failure opens a fresh one")]
    public async Task Failure_success_failure_opens_a_second_incident_rather_than_reopening_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // An orphaned attempt: the projector opens a FirstFailure incident on the slot.
        await OrphanOneAttemptAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);
        var opened = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, opened.Kind);
        Assert.Equal(slotId, opened.JobId);
        Assert.Equal(1, opened.OccurrenceCount);
        Assert.Null(opened.ResolvedAtUtc);

        // The next fire succeeds: the projector closes that incident and writes none of its own.
        await SucceedOneFireAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(opened.Id, resolved.Id);
        Assert.Equal(1, resolved.OccurrenceCount);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // A second orphan on the same key. Resolution is terminal, so this cannot land on the closed row:
        // the flap is a new incident, with a ref of its own and a count starting over at 1. The closed
        // row's resolved instant is compared, not merely re-checked for non-null, because a re-opening
        // upsert would have cleared it and a re-stamping one would have moved it.
        await OrphanOneAttemptAsync(slotId, ct);
        await RunAlertsAsync(slotId, ct);

        var rows = (await ReadAlertsAsync(NamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(opened.Id, rows[0].Id);
        Assert.Equal(resolved.ResolvedAtUtc, rows[0].ResolvedAtUtc);
        Assert.Equal(1, rows[0].OccurrenceCount);
        Assert.Equal(AlertKindCode.FirstFailure, rows[1].Kind);
        Assert.Null(rows[1].ResolvedAtUtc);
        Assert.Equal(1, rows[1].OccurrenceCount);
        Assert.NotEqual(opened.AlertRef, rows[1].AlertRef);
    }

    [Fact(DisplayName = "Replaying the batch a crashed pass never checkpointed changes nothing it already projected")]
    public async Task Replayed_batch_neither_inflates_a_count_nor_opens_a_ghost_incident()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Fail, succeed, fail on one stable slot id with no projector pass in between, so all three
        // events sit above the cursor and one batch carries the whole sequence.
        await OrphanOneAttemptAsync(slotId, ct);
        await SucceedOneFireAsync(slotId, ct);
        await OrphanOneAttemptAsync(slotId, ct);

        // The pass that projects the whole batch: raise, resolve, raise. Two incidents on one key - the
        // first closed by the success, the second opened by the failure behind it.
        await RunAlertsAsync(slotId, ct);
        var projected = (await ReadAlertsAsync(NamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, projected.Count);
        Assert.All(projected, a => Assert.Equal(AlertKindCode.FirstFailure, a.Kind));
        Assert.All(projected, a => Assert.Equal(slotId, a.JobId));
        Assert.All(projected, a => Assert.Equal(1, a.OccurrenceCount));
        Assert.NotNull(projected[0].ResolvedAtUtc);
        Assert.Null(projected[1].ResolvedAtUtc);

        // The crash: the pass committed every alert write, then died before writing its cursor back.
        // Dropping the variable restores the cursor to exactly what that pass started from - zero - so
        // the next pass re-reads the identical batch.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));

        await RunAlertsAsync(slotId, ct);

        // The replay is a no-op on every axis. The row count is pinned at 2 first, because the failure
        // this fact exists to catch is a third row: the replayed FIRST failure arrives after the
        // resolution that already absorbed it, and only the ghost guard stops it opening an incident
        // behind that resolution. The counts stay at 1 - not merely below the threshold - so a replay
        // that merely re-incremented would fail here too, the second row is still open, so the replayed
        // success did not close what a newer failure opened, and the unchanged versions prove the replay
        // wrote nothing at all rather than writing the same values back.
        var afterReplay = (await ReadAlertsAsync(NamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, afterReplay.Count);
        Assert.Equal(projected.Select(a => a.Id), afterReplay.Select(a => a.Id));
        Assert.All(afterReplay, a => Assert.Equal(1, a.OccurrenceCount));
        Assert.Equal(projected[0].ResolvedAtUtc, afterReplay[0].ResolvedAtUtc);
        Assert.Null(afterReplay[1].ResolvedAtUtc);
        Assert.Equal(projected.Select(a => a.Version), afterReplay.Select(a => a.Version));
    }

    [Fact(DisplayName = "A failure replayed after its incident resolved opens no ghost incident and escalates nothing")]
    public async Task Failure_replayed_after_resolution_opens_nothing_and_escalates_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Threshold 1, so the very first failure escalates. That is what makes the replay's silence
        // load-bearing: a ghost incident would open at count 1, meet the threshold, and be stamped with
        // the replayed event's own id - the exact shape the escalation condition fires on.
        await OrphanOneAttemptAsync(slotId, ct);
        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 1, ct);
        var opened = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Equal(2, opened.Count);
        Assert.Single(opened, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Single(opened, a => a.Kind == AlertKindCode.ThresholdReached);

        // The success closes both, stamping its own - newer - id on each.
        await SucceedOneFireAsync(slotId, ct);
        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 1, ct);
        var resolved = (await ReadAlertsAsync(NamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, resolved.Count);
        Assert.All(resolved, a => Assert.NotNull(a.ResolvedAtUtc));

        // The crash-replay re-offers the failure event from behind that resolution.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));
        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 1, ct);

        // Nothing opened and nothing escalated: the raise found the identity already marked at or past
        // this event and wrote nothing, so the count it handed back came with the resolving success's
        // mark rather than this event's - and the escalation only fires when those match.
        var afterReplay = (await ReadAlertsAsync(NamespaceId, ct)).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, afterReplay.Count);
        Assert.Equal(resolved.Select(a => a.Id), afterReplay.Select(a => a.Id));
        Assert.Equal(resolved.Select(a => a.ResolvedAtUtc), afterReplay.Select(a => a.ResolvedAtUtc));
        Assert.All(afterReplay, a => Assert.Equal(1, a.OccurrenceCount));
        Assert.Equal(resolved.Select(a => a.Version), afterReplay.Select(a => a.Version));
    }

    [Fact(
        DisplayName = "A crash between the crossing event's FirstFailure and ThresholdReached emits recovers one correctly-sourced escalation"
    )]
    public async Task Interrupted_threshold_crossing_recovers_single_sourced_not_inflated()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Three failures on one slot with no success between them, so all three land on one open
        // incident and count 1, 2, 3; the third is the true crossing event. A success between them would
        // have closed the incident and restarted the count, which is why the slot's failure counter is
        // reset directly instead - the recovery sweep terminalizes at the probe's MaxAttempts, and only
        // an uninterrupted incident can reach a threshold above 1.
        await OrphanWithoutTerminalizingAsync(slotId, ct);
        await OrphanWithoutTerminalizingAsync(slotId, ct);
        await OrphanWithoutTerminalizingAsync(slotId, ct);
        var crossingEventId = (await ReadLatestEventAsync(slotId, EventCode.JobExecutionFinished, ct)).Id;

        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 3, ct);
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
        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 3, ct);

        // Only the crossing event re-fires: the two earlier events' held raises return the stored
        // count - already at the threshold - with a newer mark and stay silent. Count 1 is asserted
        // first because inflation is the failure this fact exists to catch: under a bare
        // count-equals-threshold condition all three replayed raises re-fired, the first one minted
        // the row blaming the wrong event, and the other two inflated it to 3.
        var recovered = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(1, recovered.OccurrenceCount);
        Assert.Equal(crossingEventId, recovered.LastProjectedEventId);

        // A second replay leaves the recovered row untouched: the crossing event's re-fire now finds the
        // escalation's own identity already marked at that event, and the unchanged version proves it
        // wrote nothing.
        Assert.Equal(1, await ForgetAlertsCursorAsync(slotId, ct));
        await RunAlertsPinnedAsync(slotId, alertFailureThreshold: 3, ct);
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

    private Task OrphanOneAttemptAsync(long slotId, CancellationToken ct) =>
        AlertTestOps.OrphanOneAttemptAsync(Services, NamespaceId, slotId, ct);

    /// <summary>
    /// One orphaned attempt that leaves the slot re-armed however many times it is called: the slot's
    /// failure counter is zeroed first, so the recovery sweep re-arms rather than terminalizing at the
    /// definition's MaxAttempts. Zeroing the counter writes no event, which is the point - a success
    /// would have resolved the incident this fact is trying to grow.
    /// </summary>
    private async Task OrphanWithoutTerminalizingAsync(long slotId, CancellationToken ct)
    {
        await Db.From<JobRuntime>().Where(r => r.Id == slotId).UpdateOnlyAsync(() => new JobRuntime { FailureCount = 0 }, ct);
        await OrphanOneAttemptAsync(slotId, ct);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
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

    // ---------- driving the projector ----------

    // The compiled AlertsJob constant names the cursor variable, so the crash this spec stages keeps
    // targeting the projector's real checkpoint even if the projector renames it.
    private Task<int> ForgetAlertsCursorAsync(long slotId, CancellationToken ct) =>
        Db.From<JobCheckpoint>()
            .Where(v => v.JobId == slotId && v.Kind == JobCheckpointKindCode.Variable && v.Name == AlertsJob.CursorVariableName)
            .DeleteAsync(ct);

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain: null, ct);

    // The crash-staging facts pin the threshold instead of inheriting the container's, because their
    // expected escalations are chosen against exactly that value - and both passes of a staged crash
    // must decide with the same threshold to mean anything.
    private Task RunAlertsPinnedAsync(long cursorOwnerJobId, int alertFailureThreshold, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(
            Services,
            TestNamespace,
            NamespaceId,
            cursorOwnerJobId,
            new JobsOptions { AlertFailureThreshold = alertFailureThreshold },
            drain: null,
            ct
        );
}
