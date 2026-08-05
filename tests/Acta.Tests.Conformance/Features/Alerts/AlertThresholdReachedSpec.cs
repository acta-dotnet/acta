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
/// End-to-end conformance for the <c>ThresholdReached</c> escalation path in <c>AlertsJob</c>:
/// drives a real failing job (<c>retry-probe</c>, <c>OnFailure</c> profile, <c>MaxAttempts = 3</c>)
/// to terminal Failed, runs the projector with per-fact thresholds, and pins exact alert state.
/// Also exercises <c>RaiseJobAlert</c> / <c>ResolveJobAlerts</c> directly to prove the dedupe
/// re-open guarantee (resolved row re-opens on the same key rather than inserting a duplicate).
/// </summary>
[ConformanceSpec(
    "alert.threshold-reached",
    "ThresholdReached fires at the exact occurrence and dedupes resolved re-opens",
    Area = "Alerts",
    Contract = "AlertsJob emits exactly one ThresholdReached alert when occurrence_count hits the threshold and re-opens a resolved row rather than inserting a duplicate.",
    Arrange = "A retry-probe job with the OnFailure profile and MaxAttempts 3 is registered, with per-fact ThresholdReached thresholds of 2 and 5.",
    Act = "The job is driven to terminal Failed and the alerts projector runs, with RaiseJobAlert and ResolveJobAlerts also called directly on the same key.",
    Assert = "Exactly one ThresholdReached alert fires at the crossing occurrence and a resolved row re-opens on the same key without a duplicate."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertThresholdReachedSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "Threshold fires exactly once at the crossing occurrence")]
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

    [Fact(DisplayName = "Occurrence above threshold does not re-emit ThresholdReached")]
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

    [Fact(DisplayName = "Resolved threshold alert re-opens on the same deduplication key without inserting a duplicate")]
    public async Task Resolved_threshold_alert_reopens_within_same_window()
    {
        var ct = TestContext.Current.CancellationToken;

        // Enqueue a job to get a real jobId; not run: only the id is needed for alert rows.
        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        var jobId = job.JobId;

        // Fixed window in the past so both raises share the same bucket.
        var windowStart = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var deduplicationKey = $"auto:99:{jobId}:threshold-reached:test-{TestId}";

        // First raise: inserts the alert (occurrence 1).
        var occ1 = await AlertTestOps.RaiseAsync(
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
            windowStart,
            ct
        );
        Assert.Equal(1, occ1);

        // Capture the row id; confirm alert is unresolved.
        var afterRaise = await ReadAlertsAsync(NamespaceId, ct);
        var raised = Assert.Single(afterRaise, a => a.Kind == AlertKindCode.ThresholdReached && a.DedupeKey == deduplicationKey);
        var capturedId = raised.Id;
        Assert.Null(raised.ResolvedAtUtc);

        // Resolve the alert (simulates a recovery event closing the row).
        await Services.GetRequiredService<IAlertStore>().ResolveJobAlertsAsync(NamespaceId, jobId, ct);
        var afterResolve = await ReadAlertsAsync(NamespaceId, ct);
        var resolvedAlert = Assert.Single(afterResolve, a => a.Id == capturedId);
        Assert.NotNull(resolvedAlert.ResolvedAtUtc);

        // Second raise with the SAME deduplication key and window: must re-open the existing row.
        var occ2 = await AlertTestOps.RaiseAsync(
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
            windowStart,
            ct
        );
        Assert.Equal(2, occ2);

        // Same row id (no new row), resolved_at_utc back to NULL, occurrence_count bumped to 2.
        var afterReopen = await ReadAlertsAsync(NamespaceId, ct);
        var reopened = Assert.Single(afterReopen, a => a.Kind == AlertKindCode.ThresholdReached && a.DedupeKey == deduplicationKey);
        Assert.Equal(capturedId, reopened.Id);
        Assert.Null(reopened.ResolvedAtUtc);
        Assert.Equal(2, reopened.OccurrenceCount);
    }

    private async Task RunUntilAttemptsAsync(JobEnqueueOutcome job, Func<int> attempts, int target, CancellationToken ct)
    {
        for (var i = 0; i < target + 12 && attempts() < target; i++)
        {
            await Runtime.RunOnceAsync(job, ct);
        }
        Assert.Equal(target, attempts());
    }

    private async Task RunAlertsWithThresholdAsync(long cursorOwnerJobId, int alertFailureThreshold, CancellationToken ct)
    {
        var opts = Options.Create(
            new JobsOptions { AlertFailureThreshold = alertFailureThreshold, AlertDedupeWindow = TimeSpan.FromHours(1) }
        );
        var alertsJob = new AlertsJob(
            Services.GetRequiredService<IAlertStore>(),
            Services.GetRequiredService<IActaClock>(),
            Services.GetRequiredService<IAlertChannelRegistry>(),
            Services.GetRequiredService<IAlertTransportRegistry>(),
            opts
        );
        await alertsJob.Handle(BuildAlertsContext(cursorOwnerJobId), ct);
    }

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
            clock: Services.GetRequiredService<IActaClock>(),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }
}
