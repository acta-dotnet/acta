using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// End-to-end conformance for the <c>sys.alerts</c> generate phase: drives a real failing job
/// (<c>retry-probe</c>, <c>OnFailure</c> profile, <c>MaxAttempts = 3</c>) to terminal Failed, runs the
/// projector, and asserts it classifies the finished <c>events</c> rows into the right automatic alerts
/// - first-failure for the non-terminal re-arms (collapsed onto one row), final-failure for the terminal
/// transition - keying off the event triple, never the mutable <c>job.failure_count</c>. A success only
/// resolves the job's open failure alerts and writes none of its own. A second pass emits nothing because
/// the cursor advanced. Runs on SqlServer and Postgres.
/// </summary>
[ConformanceSpec(
    "alerts-projection.classify",
    "The alerts projector classifies failures off events and resolves on success",
    Area = "Alerts",
    Contract = "The sys.alerts projector classifies finished events into first-failure and final-failure alerts, resolves them on success, and advances its cursor.",
    Arrange = "A failing retry-probe with the OnFailure profile and a flaky-recover job are registered in the test namespace.",
    Act = "Both jobs run to their terminal outcomes and the alerts projector passes over the finished events twice.",
    Assert = "First-failures collapse onto one row, the terminal transition emits FinalFailure, success resolves without adding a row, and the second pass emits nothing."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertsProjectionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "First-failures collapse onto one row and the terminal transition emits FinalFailure")]
    public async Task Failure_lifecycle_projects_first_then_final_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        // Three attempts: two in-budget re-arms (non-terminal failures), then terminal Failed.
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 3, ct);

        await RunAlertsAsync(job.JobId, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Equal(2, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(AlertOriginCode.Automatic, a.OriginCode));
        Assert.All(alerts, a => Assert.Equal(job.JobId, a.JobId));

        var final = Assert.Single(alerts, a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Equal(AlertSeverityCode.Error, final.SeverityCode);
        Assert.Equal(1, final.OccurrenceCount);

        var first = Assert.Single(alerts, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Equal(AlertSeverityCode.Warning, first.SeverityCode);
        Assert.Equal(2, first.OccurrenceCount); // two non-terminal failures collapsed onto one row
    }

    [Fact(DisplayName = "Cursor advance stops re-emission on a second pass")]
    public async Task Cursor_advances_so_a_second_pass_emits_nothing_new()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 3, ct);

        await RunAlertsAsync(job.JobId, ct);
        var afterFirst = (await ReadAlertsAsync(NamespaceId, ct)).Count;

        await RunAlertsAsync(job.JobId, ct);
        var afterSecond = (await ReadAlertsAsync(NamespaceId, ct)).Count;

        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact(DisplayName = "Worker config provides the default channel")]
    public void Worker_config_provides_default_channel()
    {
        var registry = Services.GetRequiredService<IAlertChannelRegistry>();
        var def = registry.Resolve(TestNamespace, "default");
        Assert.NotNull(def);
        Assert.Equal(AlertChannelStatusCode.Active, def!.Status);
        Assert.Equal("log", def.TransportKind);
    }

    [Fact(DisplayName = "Default channel job alert is delivered")]
    public async Task Default_channel_job_alert_is_delivered()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => RetryProbe.Attempts(TestNamespace), 1, ct);

        // Generate + deliver: the OnFailure job has no explicit channel, so it routes to the seeded
        // "default" (log transport) and the deliver phase marks it Delivered.
        await RunAlertsAsync(job.JobId, ct);

        var alerts = await ReadAlertsAsync(NamespaceId, ct);
        Assert.NotEmpty(alerts);
        Assert.All(alerts, a => Assert.Equal("default", a.ChannelName));
        Assert.All(alerts, a => Assert.Equal(AlertDeliveryStatusCode.Delivered, a.DeliveryStatusCode));
    }

    [Fact(DisplayName = "Success resolves the open failure and writes no new alert")]
    public async Task Success_resolves_open_failure_and_writes_no_new_alert()
    {
        var ct = TestContext.Current.CancellationToken;
        FlakyRecoverProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);

        // Attempt 1 fails -> re-arm. Project it: an open FirstFailure alert (unresolved).
        await RunUntilAttemptsAsync(job, () => FlakyRecoverProbe.Attempts(TestNamespace), 1, ct);
        await RunAlertsAsync(job.JobId, ct);
        var afterFail = await ReadAlertsAsync(NamespaceId, ct);
        var firstFailure = Assert.Single(afterFail, a => a.Kind == AlertKindCode.FirstFailure);
        Assert.Null(firstFailure.ResolvedAtUtc);

        // Attempt 2 succeeds. Project it: the open failure resolves and nothing new is written.
        await RunUntilAttemptsAsync(job, () => FlakyRecoverProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        // "Nothing new" is proven positively, by pinning the whole alert set: Assert.Single over the
        // collection (not Assert.All, which passes vacuously on zero matches) shows the FirstFailure row
        // from before is still the ONLY row, then asserts it resolved. Asserting a kind is *absent*
        // would be unfalsifiable - it would pass even if projection stopped working entirely.
        var afterSuccess = await ReadAlertsAsync(NamespaceId, ct);
        var resolvedFirstFailure = Assert.Single(afterSuccess);
        Assert.Equal(AlertKindCode.FirstFailure, resolvedFirstFailure.Kind);
        Assert.Equal(job.JobId, resolvedFirstFailure.JobId);
        Assert.NotNull(resolvedFirstFailure.ResolvedAtUtc);

        // A further pass with no new events still writes nothing: the same single resolved row.
        await RunAlertsAsync(job.JobId, ct);
        var afterIdle = await ReadAlertsAsync(NamespaceId, ct);
        var stillOnlyRow = Assert.Single(afterIdle);
        Assert.Equal(AlertKindCode.FirstFailure, stillOnlyRow.Kind);
        Assert.NotNull(stillOnlyRow.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A None alert-profile job projects no alerts")]
    public async Task None_profile_job_projects_no_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        NoAlertProbe.Reset(TestNamespace);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "no-alert-probe", JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => NoAlertProbe.Attempts(TestNamespace), 1, ct);

        await RunAlertsAsync(job.JobId, ct);

        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
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
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain: null, ct);
}
