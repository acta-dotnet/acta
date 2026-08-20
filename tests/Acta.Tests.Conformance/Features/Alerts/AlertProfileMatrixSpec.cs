using Acta.Runtime.Modules.Alerting;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for alert profile gating: <c>OnTerminal</c> and <c>Info</c> profiles suppress
/// non-terminal failures and only emit on terminal transitions; <c>SysCritical</c> always emits
/// at Critical severity for every failure transition; and a resolved FinalFailure alert stays resolved
/// when the same deduplication key fires again - that firing opens a second incident row of its own.
/// </summary>
[ConformanceSpec(
    "alert.profile-matrix",
    "Alert profiles gate emission and severity per profile",
    Area = "Alerts",
    Contract = "Each alert profile gates non-terminal emission and severity, and a re-fire on a resolved alert's key opens a fresh incident rather than re-opening it.",
    Arrange = "Probe jobs with OnTerminal, Info, and SysCritical alert profiles are registered in the test namespace.",
    Act = "Each probe fails non-terminally then terminally with the projector run after each attempt, and a resolved FinalFailure re-fires on its deduplication key.",
    Assert = "OnTerminal and Info emit only a terminal FinalFailure at their profile severity, SysCritical always emits Critical, and a resolved alert stays resolved."
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

    [Fact(DisplayName = "A resolved OnTerminal FinalFailure stays resolved and the same key's next firing opens a second incident")]
    public async Task Resolved_alert_is_not_reopened_by_a_later_raise_on_its_key()
    {
        var ct = TestContext.Current.CancellationToken;
        OnTerminalProbe.Reset(TestNamespace);

        // Drive to terminal (2 attempts) to produce a FinalFailure alert.
        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "on-terminal-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => OnTerminalProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        // Capture the emitted alert's incident identity.
        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        var seeded = Assert.Single(alerts, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Equal(1, seeded.OccurrenceCount);
        Assert.Null(seeded.ResolvedAtUtc);
        Assert.NotNull(seeded.DedupeKey);

        // Resolve it, standing in for the success event that would close it: an id past every failure
        // event the projector already stamped on the row.
        await Services
            .GetRequiredService<IAlertStore>()
            .ResolveJobAlertsAsync(NamespaceId, job.JobId, await NextEventIdAsync(job.JobId, ct), ct);
        var afterResolve = await ReadAlertsAsync(NamespaceId, ct);
        var resolved = Assert.Single(afterResolve, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // Re-raise on the identical deduplication key. Resolution is terminal, so this cannot land on the
        // closed row: it opens a second incident beside it.
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
            seeded.DedupeKey,
            ct
        );

        // Two rows on the one key: the first still closed at the instant it closed, the second open and
        // counting from 1. The resolved timestamp is asserted equal, not merely non-null, because a raise
        // that re-stamped it would look identical to one that left it alone.
        var afterRefire = await ReadAlertsAsync(NamespaceId, ct);
        var incidents = afterRefire.Where(a => a.Kind == AlertKindCode.FinalFailure).OrderBy(a => a.Id).ToList();
        Assert.Equal(2, incidents.Count);
        Assert.Equal(seeded.Id, incidents[0].Id);
        Assert.Equal(resolved.ResolvedAtUtc, incidents[0].ResolvedAtUtc);
        Assert.Equal(1, incidents[0].OccurrenceCount);
        Assert.Null(incidents[1].ResolvedAtUtc);
        Assert.Equal(1, incidents[1].OccurrenceCount);
        Assert.NotEqual(seeded.AlertRef, incidents[1].AlertRef);
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

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, ct);
}
