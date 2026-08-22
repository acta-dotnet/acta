using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// End-to-end conformance for the <c>ThresholdReached</c> escalation path in <c>AlertsJob</c>:
/// drives a real failing job (<c>retry-probe</c>, <c>OnFailure</c> profile, <c>MaxAttempts = 3</c>)
/// to terminal Failed, runs the projector with per-fact thresholds, and pins exact alert state.
/// Also proves the count is per-incident from both ends: at the store, where it climbs only while the
/// incident is open and restarts at 1 in the row that opens after a resolution; and end to end on a
/// recurring slot, where that restart is what makes the projector emit a second <c>ThresholdReached</c>
/// for the next outage instead of falling silent once a key has escalated.
/// </summary>
[ConformanceSpec(
    "alert.threshold-reached",
    "ThresholdReached fires once per incident at the exact occurrence",
    Area = "Alerts",
    Contract = "AlertsJob emits one ThresholdReached alert when occurrence_count hits the threshold, and the count restarts at 1 in the incident that opens after a resolution.",
    Arrange = "A retry-probe job with the OnFailure profile and MaxAttempts 3 is registered, with per-fact ThresholdReached thresholds of 1, 2, and 5.",
    Act = "The job is driven to terminal Failed with the projector running, and a recurring slot is then failed, recovered, and failed again across projector passes.",
    Assert = "One ThresholdReached fires at the crossing occurrence, further failures in that incident do not re-fire it, and the next incident emits one of its own."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertThresholdReachedSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "Threshold fires exactly once per incident, at the crossing occurrence")]
    public async Task Threshold_emits_exactly_once_above_does_not_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        // Three attempts: two non-terminal re-arms (hit threshold at 2) then one terminal Failed.
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 3, ct);

        // Threshold=2: first non-terminal fires FirstFailure (occurrence 1), second fires FirstFailure
        // (occurrence 2 == threshold) PLUS ThresholdReached; third is terminal (FinalFailure only, no repeat).
        await RunAlertsWithThresholdAsync(job.JobId, alertFailureThreshold: 2, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);

        // Exactly three alerts: FirstFailure + ThresholdReached + FinalFailure.
        Assert.Equal(3, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(AlertOriginCode.Automatic, a.OriginCode));

        var threshold = Assert.Single(alerts, a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(AlertSeverityCode.Error, threshold.SeverityCode);
        Assert.Equal(1, threshold.OccurrenceCount);
        Assert.Contains("threshold-reached", threshold.DedupeKey);

        // FirstFailure.OccurrenceCount == 2 proves both non-terminal failure events were processed,
        // confirming the single ThresholdReached (not missing events, not over-counting).
        var first = Assert.Single(alerts, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Equal(2, first.OccurrenceCount);
    }

    [Fact(DisplayName = "A further failure in the same incident does not re-emit ThresholdReached")]
    public async Task Occurrence_above_threshold_does_not_re_emit_threshold_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        // Three attempts with a threshold of one exercise the threshold crossing, the suppressed
        // above-threshold branch, and the terminal final-failure branch.
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 3, ct);
        await RunAlertsWithThresholdAsync(job.JobId, alertFailureThreshold: 1, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);

        // Exactly three alerts: FirstFailure + ThresholdReached + FinalFailure.
        Assert.Equal(3, alerts.Count);

        var threshold = Assert.Single(alerts, a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(AlertSeverityCode.Error, threshold.SeverityCode);
        Assert.Equal(AlertOriginCode.Automatic, threshold.OriginCode);
        Assert.Equal(AlertKindCode.ThresholdReached, threshold.Kind);
        Assert.Equal(1, threshold.OccurrenceCount);
        Assert.Contains("threshold-reached", threshold.DedupeKey);

        // FirstFailure.OccurrenceCount == 2 proves the above-threshold occurrence was processed
        // but did not emit a second ThresholdReached: the suppression guard ran.
        var first = Assert.Single(alerts, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Equal(2, first.OccurrenceCount);
    }

    [Fact(DisplayName = "Below-threshold drive emits no ThresholdReached alert")]
    public async Task Below_threshold_emits_no_threshold_reached_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 3, ct);

        // Threshold=5: 3 non-terminal failures never reach it, so ThresholdReached is never emitted.
        await RunAlertsWithThresholdAsync(job.JobId, alertFailureThreshold: 5, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Equal(2, alerts.Count);
        Assert.Empty(alerts.Where(a => a.Kind == AlertKindCode.ThresholdReached));

        // FirstFailure.OccurrenceCount == 2 proves events were processed; threshold just wasn't reached.
        var first = Assert.Single(alerts, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Equal(2, first.OccurrenceCount);
    }

    [Fact(DisplayName = "A fresh incident escalates again: the projector emits a second ThresholdReached after a resolution")]
    public async Task A_fresh_incident_after_a_resolution_emits_its_own_threshold_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);

        // A recurring slot, because only a job that outlives its own success can break, recover, and
        // break again on one job id - and the identity is per job id, so a second enqueue would be a
        // different incident lineage rather than the same one re-opening.
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, "recurring-ping", ct);

        // Threshold 1: the first failure of an incident crosses it, so each incident should escalate
        // exactly once and the second escalation is the whole subject of this fact.
        await AlertTestOps.OrphanOneAttemptAsync(Services, NamespaceId, slotId, ct);
        await RunAlertsWithThresholdAsync(slotId, alertFailureThreshold: 1, ct);
        var firstEscalation = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Kind == AlertKindCode.ThresholdReached);
        Assert.Equal(1, firstEscalation.OccurrenceCount);
        Assert.Null(firstEscalation.ResolvedAtUtc);

        // The success closes both incidents the failure opened.
        await SucceedOneFireAsync(slotId, ct);
        await RunAlertsWithThresholdAsync(slotId, alertFailureThreshold: 1, ct);
        Assert.All(await ReadAlertsAsync(NamespaceId, ct), a => Assert.NotNull(a.ResolvedAtUtc));

        // A genuinely new failure. The count restarting at 1 is what makes the threshold reachable
        // again, so the projector emits a SECOND ThresholdReached row rather than finding the key
        // already at or past the threshold and staying silent forever.
        await AlertTestOps.OrphanOneAttemptAsync(Services, NamespaceId, slotId, ct);
        await RunAlertsWithThresholdAsync(slotId, alertFailureThreshold: 1, ct);

        var escalations = (await ReadAlertsAsync(NamespaceId, ct))
            .Where(a => a.Kind == AlertKindCode.ThresholdReached)
            .OrderBy(a => a.Id)
            .ToList();
        Assert.Equal(2, escalations.Count);
        Assert.Equal(firstEscalation.Id, escalations[0].Id);
        Assert.NotNull(escalations[0].ResolvedAtUtc);
        Assert.Null(escalations[1].ResolvedAtUtc);
        Assert.Equal(1, escalations[1].OccurrenceCount);
        Assert.NotEqual(firstEscalation.AlertRef, escalations[1].AlertRef);
    }

    /// <summary>
    /// One successful fire of the slot. The reclaim leaves it claimable immediately; no schedule is due,
    /// so the handler runs with an empty triggering set and the slot rolls over to Ready.
    /// </summary>
    private async Task SucceedOneFireAsync(long slotId, CancellationToken ct)
    {
        var before = RecurringPingHandler.TriggersFor(TestNamespace).Count;
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(before + 1, RecurringPingHandler.TriggersFor(TestNamespace).Count);
    }

    [Fact(DisplayName = "The count climbs only inside one incident: after a resolution the same key starts a new row at 1")]
    public async Task Occurrence_count_restarts_in_the_incident_that_opens_after_a_resolution()
    {
        var ct = TestContext.Current.CancellationToken;

        // Enqueue a job to get a real jobId; not run: only the id is needed for alert rows.
        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        var jobId = job.JobId;
        var deduplicationKey = $"auto:99:{jobId}:threshold-reached:test-{TestId}";

        // The incident opens, then absorbs a repeat: the count the escalation reads is 1 then 2.
        Assert.Equal(1, (await RaiseThresholdAsync(jobId, deduplicationKey, ct)).OccurrenceCount);
        Assert.Equal(2, (await RaiseThresholdAsync(jobId, deduplicationKey, ct)).OccurrenceCount);

        var opened = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.DedupeKey == deduplicationKey);
        Assert.Null(opened.ResolvedAtUtc);

        // Resolve it (simulates a recovery event closing the row).
        await Services.GetRequiredService<IAlertStore>().ResolveJobAlertsAsync(NamespaceId, jobId, await NextEventIdAsync(jobId, ct), ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Id == opened.Id);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // The same key again. Resolution is terminal, so this is a NEW incident: a new row counting from
        // 1, which is what lets the threshold be crossed a second time rather than the count carrying the
        // old incident's total past it forever.
        Assert.Equal(1, (await RaiseThresholdAsync(jobId, deduplicationKey, ct)).OccurrenceCount);
        Assert.Equal(2, (await RaiseThresholdAsync(jobId, deduplicationKey, ct)).OccurrenceCount);

        var rows = (await ReadAlertsAsync(NamespaceId, ct)).Where(a => a.DedupeKey == deduplicationKey).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(opened.Id, rows[0].Id);
        Assert.Equal(resolved.ResolvedAtUtc, rows[0].ResolvedAtUtc); // never cleared back to NULL
        Assert.Equal(2, rows[0].OccurrenceCount);
        Assert.Null(rows[1].ResolvedAtUtc);
        Assert.Equal(2, rows[1].OccurrenceCount);
        Assert.NotEqual(rows[0].AlertRef, rows[1].AlertRef);
    }

    private Task<AlertRaiseOutcome> RaiseThresholdAsync(long jobId, string deduplicationKey, CancellationToken ct) =>
        AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            jobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.ThresholdReached,
            "Threshold test",
            "Repeated failures.",
            "default",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            ct
        );

    private async Task RunUntilAttemptsAsync(JobEnqueueOutcome job, Func<int> attempts, int target, CancellationToken ct)
    {
        for (var i = 0; i < target + 12 && attempts() < target; i++)
        {
            await Runtime.RunOnceAsync(job, ct);
        }
        Assert.Equal(target, attempts());
    }

    // Per-fact thresholds ride an options override into the shared driver. No success interrupts the
    // drives below, so every failure a fact produces lands on one open incident and the counts are
    // deterministic without pinning any clock.
    private Task RunAlertsWithThresholdAsync(long cursorOwnerJobId, int alertFailureThreshold, CancellationToken ct) =>
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
