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
/// End-to-end conformance for the <c>sys.alerts</c> generate phase: drives a real failing job
/// (<c>retry-probe</c>, <c>OnFailure</c> profile, <c>MaxAttempts = 3</c>) to terminal Failed, runs the
/// projector, and asserts it classifies the finished <c>events</c> rows into the right automatic alerts
/// - first-failure for the non-terminal re-arms (collapsed onto one row), final-failure for the terminal
/// transition - keying off the event triple, never the mutable <c>job.failure_count</c>. A second pass
/// emits nothing because the cursor advanced. Runs on SqlServer and Postgres.
/// </summary>
[ConformanceSpec(
    "alerts-projection.classify",
    "The alerts projector classifies failures and recoveries off events",
    Area = "Alerts",
    Contract = "The sys.alerts projector classifies finished events into first-failure, final-failure and recovery alerts, advances its cursor so a second pass emits nothing.",
    Arrange = "A failing retry-probe with the OnFailure profile and a flaky-recover job are registered in the test namespace.",
    Act = "Both jobs run to their terminal outcomes and the alerts projector passes over the finished events twice.",
    Assert = "First-failures collapse onto one row, the terminal transition emits FinalFailure, success emits Recovery, and the second pass emits nothing."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class AlertsProjectionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

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

    [Fact(DisplayName = "Success resolves the open failure and emits one Recovery")]
    public async Task Success_resolves_open_failure_and_emits_one_recovery()
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

        // Attempt 2 succeeds. Project it: the open failure resolves and exactly one recovery is emitted.
        await RunUntilAttemptsAsync(job, () => FlakyRecoverProbe.Attempts(TestNamespace), 2, ct);
        await RunAlertsAsync(job.JobId, ct);

        var afterSuccess = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Single(afterSuccess, a => a.Kind == AlertKindCode.Recovery);
        Assert.All(afterSuccess.Where(a => a.Kind == AlertKindCode.FirstFailure), a => Assert.NotNull(a.ResolvedAtUtc));

        // A further pass with no new events emits no second recovery.
        await RunAlertsAsync(job.JobId, ct);
        var afterIdle = await ReadAlertsAsync(NamespaceId, ct);
        Assert.Single(afterIdle, a => a.Kind == AlertKindCode.Recovery);
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
            alertStore: Services.GetRequiredService<IAlertStore>(),
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
