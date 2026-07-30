using Acta.Configuration;
using Acta.Features.Alerts;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Signals;
using Acta.Payloads;
using Acta.Services.Locks;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for alert profile gating: <c>OnTerminal</c> and <c>Info</c> profiles suppress
/// non-terminal failures and only emit on terminal transitions; <c>SysCritical</c> always emits
/// at Critical severity for every failure transition; and a resolved FinalFailure alert re-opens
/// (<c>resolved_at_utc</c> → NULL, <c>occurrence_count</c> incremented) when the same deduplication key fires
/// again within the window.
/// </summary>
[ConformanceSpec(
    "alert.profile-matrix",
    "Alert profiles gate emission and severity per profile",
    Area = "Alerts",
    Contract = "Each alert profile gates non-terminal emission and severity, and a resolved alert re-opens when the same deduplication key re-fires within the window.",
    Arrange = "Probe jobs with OnTerminal, Info, and SysCritical alert profiles are registered in the test namespace.",
    Act = "Each probe fails non-terminally then terminally with the projector run after each attempt, and a resolved FinalFailure re-fires on its deduplication key.",
    Assert = "OnTerminal and Info emit only a terminal FinalFailure at their profile severity, SysCritical always emits Critical, and the resolved alert re-opens."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertProfileMatrixSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "OnTerminal emits no alert on non-terminal failure then one FinalFailure Error on terminal")]
    public async Task OnTerminal_skips_non_terminal_then_emits_final_failure_on_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        OnTerminalProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "on-terminal-probe", JobPayload.None), ct);

        // Attempt 1: non-terminal failure. OnTerminal suppresses it: zero alerts.
        await RunUntilAttemptsAsync(job, () => OnTerminalProbe.Attempts(TestNamespace), 1, ct);
        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));

        // Attempt 2: terminal Failed. OnTerminal emits exactly one FinalFailure at Error.
        await RunUntilAttemptsAsync(job, () => OnTerminalProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        var final = Assert.Single(alerts);
        Assert.Equal(AlertKindCode.FinalFailure, final.Kind);
        Assert.Equal(AlertSeverityCode.Error, final.SeverityCode);
        Assert.Equal(1, final.OccurrenceCount);
        Assert.Null(final.ResolvedAtUtc);
    }

    [Fact(DisplayName = "Info emits no alert on non-terminal failure then one FinalFailure at Info severity on terminal")]
    public async Task Info_skips_non_terminal_then_emits_final_failure_at_info_severity()
    {
        var ct = TestContext.Current.CancellationToken;
        InfoAlertProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "info-alert-probe", JobPayload.None), ct);

        // Attempt 1: non-terminal failure. Info suppresses it: zero alerts.
        await RunUntilAttemptsAsync(job, () => InfoAlertProbe.Attempts(TestNamespace), 1, ct);
        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));

        // Attempt 2: terminal Failed. Info emits exactly one FinalFailure at Info severity (not Error).
        await RunUntilAttemptsAsync(job, () => InfoAlertProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        var final = Assert.Single(alerts);
        Assert.Equal(AlertKindCode.FinalFailure, final.Kind);
        Assert.Equal(AlertSeverityCode.Info, final.SeverityCode);
        Assert.Equal(1, final.OccurrenceCount);
        Assert.Null(final.ResolvedAtUtc);
    }

    [Fact(DisplayName = "SysCritical emits Critical FirstFailure on non-terminal and Critical FinalFailure on terminal")]
    public async Task SysCritical_emits_critical_first_failure_and_critical_final_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        SysCriticalProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "sys-critical-probe", JobPayload.None), ct);

        // Attempt 1: non-terminal failure. SysCritical emits a FirstFailure at Critical.
        await RunUntilAttemptsAsync(job, () => SysCriticalProbe.Attempts(TestNamespace), 1, ct);
        await RunAlertsAsync(job.JobId, ct);

        var afterFirst = await ReadAlertsAsync(NamespaceId, ct);
        var firstFailure = Assert.Single(afterFirst);
        Assert.Equal(AlertKindCode.FirstFailure, firstFailure.Kind);
        Assert.Equal(AlertSeverityCode.Critical, firstFailure.SeverityCode);
        Assert.Equal(1, firstFailure.OccurrenceCount);

        // Attempt 2: terminal Failed. SysCritical adds a FinalFailure at Critical; FirstFailure stays.
        await RunUntilAttemptsAsync(job, () => SysCriticalProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        var afterTerminal = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Equal(2, afterTerminal.Count);
        var finalFailure = Assert.Single(afterTerminal, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Equal(AlertSeverityCode.Critical, finalFailure.SeverityCode);
        Assert.Equal(1, finalFailure.OccurrenceCount);
        Assert.Null(finalFailure.ResolvedAtUtc);
        Assert.All(afterTerminal, a => Assert.Equal(AlertSeverityCode.Critical, a.SeverityCode));
    }

    [Fact(DisplayName = "Resolved OnTerminal FinalFailure re-opens with incremented occurrence_count when the same key re-fires")]
    public async Task Resolved_alert_reopens_on_same_key_reupsert_within_dedupe_window()
    {
        var ct = TestContext.Current.CancellationToken;
        OnTerminalProbe.Reset(TestNamespace);

        // Drive to terminal (2 attempts) to produce a FinalFailure alert.
        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "on-terminal-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => OnTerminalProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        // Capture the emitted alert's dedupe coordinates.
        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        var seeded = Assert.Single(alerts, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Equal(1, seeded.OccurrenceCount);
        Assert.Null(seeded.ResolvedAtUtc);
        Assert.NotNull(seeded.DeduplicationKey);
        Assert.NotNull(seeded.DedupeWindowStartUtc);

        // Resolve it.
        await Services.GetRequiredService<IAlertStore>().ResolveJobAlertsAsync(NamespaceId, job.JobId, ct);
        var afterResolve = await ReadAlertsAsync(NamespaceId, ct);
        Assert.NotNull(Assert.Single(afterResolve, a => a.Kind == AlertKindCode.FinalFailure).ResolvedAtUtc);

        // Re-raise with the identical deduplication key within the same window: re-opens and bumps occurrence_count.
        await AlertTestOps.RaiseAsync(
            Services,
            TestNamespace,
            job.JobId,
            AlertOriginCode.Automatic,
            AlertSeverityCode.Error,
            AlertKindCode.FinalFailure,
            seeded.Title,
            seeded.Message,
            seeded.ChannelName,
            AlertDeliveryStatusCode.Pending,
            seeded.DeduplicationKey,
            seeded.DedupeWindowStartUtc,
            ct
        );

        // Same single row: re-opened (resolved_at_utc = NULL) with occurrence_count = 2.
        var afterReopen = await ReadAlertsAsync(NamespaceId, ct);
        var reopened = Assert.Single(afterReopen, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Null(reopened.ResolvedAtUtc);
        Assert.Equal(2, reopened.OccurrenceCount);
    }

    // RunOnceAsync can no-op when a claim is lost to provider timing (notably MSSQL); loop until the probe
    // has actually executed `target` attempts so the projected event count is deterministic.
    private async Task RunUntilAttemptsAsync(JobEnqueueOutcome job, Func<int> attempts, int target, CancellationToken ct)
    {
        for (var i = 0; i < target + 12 && attempts() < target; i++)
        {
            await Runtime.RunOnceAsync(job, ct);
        }
        Assert.Equal(target, attempts());
    }

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
            alertDedupeWindow: TimeSpan.FromHours(1),
            cancellationToken: CancellationToken.None,
            triggeringScheduleNames: [],
            deadlineAtUtc: null
        );
    }
}
