using Acta.Runtime.Modules.Alerting;
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
/// Also exercises <c>RaiseJobAlert</c> / <c>ResolveJobAlerts</c> directly to prove that the count is
/// per-incident: it climbs only while the incident is open, and the incident that opens after a
/// resolution starts back at 1 and can therefore escalate again.
/// </summary>
[ConformanceSpec(
    "alert.threshold-reached",
    "ThresholdReached fires once per incident at the exact occurrence",
    Area = "Alerts",
    Contract = "AlertsJob emits one ThresholdReached alert when occurrence_count hits the threshold, and the count restarts at 1 in the incident that opens after a resolution.",
    Arrange = "A retry-probe job with the OnFailure profile and MaxAttempts 3 is registered, with per-fact ThresholdReached thresholds of 1, 2, and 5.",
    Act = "The job is driven to terminal Failed and the alerts projector runs, with RaiseJobAlert and ResolveJobAlerts also called directly on one key across a resolution.",
    Assert = "One ThresholdReached fires at the crossing occurrence, further failures in that incident do not re-fire it, and the next incident counts from 1 again."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertThresholdReachedSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

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

    [Fact(
        DisplayName = "The count climbs only inside one incident: after a resolution the same key starts a new row at 1 and can escalate again"
    )]
    public async Task Occurrence_count_is_per_incident_so_the_next_incident_can_escalate_again()
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
            ct
        );
}
