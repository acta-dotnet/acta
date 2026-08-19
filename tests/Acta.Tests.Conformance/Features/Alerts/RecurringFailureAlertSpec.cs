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
/// Conformance for the failure shape nobody is watching: a recurring slot whose handler throws. The
/// slot re-arms Ready for its next occurrence rather than terminalizing, so the alert has to come off
/// the reason the rollover event records - a Ready transition with no reason is invisible to
/// <c>GetAlertableEvents</c>, and a nightly job could fail every night without ever raising one. The
/// subject is that the whole chain holds end to end: the throw is attributed on the rollover, the
/// projector sees it, and the repeat lands on the same row instead of going unnoticed.
/// </summary>
[ConformanceSpec(
    "alerts-projection.recurring-failure",
    "A recurring job whose handler throws raises an alert",
    Area = "Alerts",
    Contract = "A recurring slot re-arming Ready after a failed fire records why that attempt failed, so the projector can alert on a job that would otherwise fail unwatched.",
    Arrange = "A recurring-ping slot in a namespace holding no alerts, with its handler armed to throw on every fire.",
    Act = "The slot fires twice, throwing both times, and the sys.alerts projector runs after each fire.",
    Assert = "Each rollover event carries the unhandled-exception reason, and the namespace holds exactly one open FirstFailure alert counting both fires."
)]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.RaiseJobAlertAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class RecurringFailureAlertSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // The manifest's recurring slot: one stable job id that survives every failed fire, which is the
    // whole reason this failure shape exists as something separate from a one-off's retry.
    private const string JobName = "recurring-ping";

    private short NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A recurring fire whose handler throws attributes the reason on its Ready rollover and raises one alert")]
    public async Task Recurring_handler_throw_is_attributed_and_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Throw on every fire. MaxAttempts is the one-off retry budget and never terminalizes a
        // recurring slot, so every one of these lands as a Ready rollover, not a terminal Failed.
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = int.MaxValue;

        // First fire: the slot re-arms for its next occurrence, and the rollover event says why the
        // attempt that just ended ended. Both halves matter - the status alone is indistinguishable
        // from a clean fire, and only the reason makes the failure visible to the projector.
        await FailOneFireAsync(slotId, expectedFires: 1, ct);

        var afterFirst = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, afterFirst.Status);
        Assert.Equal((short)1, afterFirst.FailureCount);

        var firstEvent = await ReadLatestEventAsync(slotId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(ExecutionStatusCode.Failed, firstEvent.ExecutionStatus);
        Assert.Equal(JobStatusCode.Ready, firstEvent.ToStatus);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, firstEvent.ReasonCode);
        Assert.NotNull(firstEvent.ReasonMessage);

        // The projector raises on it: a non-terminal failure under the default OnFailure profile is a
        // FirstFailure at Warning, open, against this slot.
        await RunAlertsAsync(slotId, ct);

        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Equal(AlertSeverityCode.Warning, raised.SeverityCode);
        Assert.Equal(slotId, raised.JobId);
        Assert.Equal(1, raised.OccurrenceCount);
        Assert.Null(raised.ResolvedAtUtc);

        // The night after: the slot fails again on the same id. The repeat is counted on the one row
        // the first failure opened rather than passing unseen, which is what "fails every night" has
        // to look like to an operator. Two is still under AlertFailureThreshold, so the set stays a
        // single row and Assert.Single keeps pinning the whole namespace, not just a match within it.
        await FailOneFireAsync(slotId, expectedFires: 2, ct);

        var secondEvent = await ReadLatestEventAsync(slotId, EventCode.JobExecutionFinished, ct);
        Assert.Equal(JobStatusCode.Ready, secondEvent.ToStatus);
        Assert.Equal(JobEventReasonCode.JobUnhandledException, secondEvent.ReasonCode);

        await RunAlertsAsync(slotId, ct);

        var repeated = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(raised.Id, repeated.Id);
        Assert.Equal(AlertKindCode.FirstFailure, repeated.Kind);
        Assert.Equal(2, repeated.OccurrenceCount);
        Assert.Null(repeated.ResolvedAtUtc);
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
    /// One fire of the slot whose handler throws. A claim can be lost to provider timing, so the loop
    /// is driven by the handler's own fire count rather than by <c>RunOnceAsync</c>'s return: the
    /// handler records the fire before it throws, making the count the one signal that the attempt
    /// really ran.
    /// </summary>
    private async Task FailOneFireAsync(long slotId, int expectedFires, CancellationToken ct)
    {
        await MakeSlotClaimableAsync(slotId, ct);
        for (var i = 0; i < 12 && RecurringPingHandler.TriggersFor(TestNamespace).Count < expectedFires; i++)
        {
            await Runtime.RunOnceAsync(slotId, ct);
        }

        Assert.Equal(expectedFires, RecurringPingHandler.TriggersFor(TestNamespace).Count);
    }

    // The harness parks seeded slots a day out; pull this one back so the by-id claim, which filters on
    // next_run_at_utc like every other claim, can take it.
    private async Task MakeSlotClaimableAsync(long slotId, CancellationToken ct)
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        await Db.From<JobRuntime>().Where(r => r.Id == slotId).UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = due }, ct);
    }

    // ---------- driving the projector ----------

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
