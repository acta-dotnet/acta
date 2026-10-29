using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Alerting;

/// <summary>
/// Conformance for the failure shape nobody is watching: a permanent slot whose handler throws. A firing
/// re-arms within its budget and a spent budget returns the slot to Idle rather than terminalizing it, so the
/// alert has to come off the reason the attempt's event records - a landing with no reason is invisible to
/// <c>GetAlertableEvents</c>, and a nightly job could fail every night without ever raising one. The
/// subject is that the whole chain holds end to end: the throw is attributed on the attempt, the
/// projector sees it, and the repeat lands on the same row instead of going unnoticed.
/// </summary>
[ConformanceSpec(
    "alerts-projection.recurring-failure",
    "A recurring job whose handler throws raises an alert",
    Area = "Alerts",
    Contract = "A permanent slot's failed attempt records why it failed, re-armed or returned to Idle, so the projector can alert on a job that would otherwise fail unwatched."
)]
public sealed class RecurringFailureAlertSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    // The manifest's recurring slot: one stable job id that survives every failed fire, which is the
    // whole reason this failure shape exists as something separate from a one-off's retry.
    private const string JobName = "recurring-ping";

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A firing whose handler throws attributes the reason on each failed attempt and raises one alert")]
    public async Task Recurring_handler_throw_is_attributed_and_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        RecurringPingHandler.Reset(TestNamespace);
        var slotId = await SlotIdAsync(ct);

        // Throw on every attempt. A firing spends MaxAttempts like a one-shot, but a spent budget returns the
        // slot to Idle for its next firing, never to a terminal Failed.
        RecurringPingHandler.FailWhileSequenceAtMost[TestNamespace] = int.MaxValue;

        // First attempt: the firing re-arms for its second, and the attempt's event says why it failed.
        // Both halves matter - the status alone is indistinguishable from a clean fire, and only the
        // reason makes the failure visible to the projector.
        await FailOneFireAsync(slotId, expectedFires: 1, ct);

        var afterFirst = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, afterFirst.Status);
        Assert.Equal(1, afterFirst.FailureCount);

        var firstEvent = await ReadLatestEventAsync(slotId, EventCode.JobExecutionFailed, ct);
        Assert.Equal(JobStatusCode.Ready.Code, EventDetail(firstEvent, "to"));
        Assert.Equal(EventReason.UnhandledException, EventDetail(firstEvent, "reason"));
        Assert.NotNull(firstEvent.EventMessage);

        // The projector raises on it: a non-terminal failure under the default OnFailure profile is a
        // FirstFailure at Warning, open, against this slot.
        await RunAlertsAsync(slotId, ct);

        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Equal(AlertSeverityCode.Warning, raised.SeverityCode);
        Assert.Equal(slotId, raised.JobId);
        Assert.Equal(1, raised.OccurrenceCount);
        Assert.Null(raised.ResolvedAtUtc);

        // The second attempt spends the budget and returns the slot to Idle on the same id. The repeat is
        // counted on the one row the first failure opened rather than passing unseen, which is what "fails
        // every night" has to look like to an operator. Two is still under AlertFailureThreshold, so the set
        // stays a single row and Assert.Single keeps pinning the whole namespace, not just a match within it.
        await FailOneFireAsync(slotId, expectedFires: 2, ct);

        var secondEvent = await ReadLatestEventAsync(slotId, EventCode.JobExecutionFailed, ct);
        Assert.Equal(JobStatusCode.Idle.Code, EventDetail(secondEvent, "to"));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(EventReason.UnhandledException, EventDetail(secondEvent, "reason"));

        await RunAlertsAsync(slotId, ct);

        var repeated = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(raised.Id, repeated.Id);
        Assert.Equal(AlertKindCode.FirstFailure, repeated.Kind);
        Assert.Equal(2, repeated.OccurrenceCount);
        Assert.Null(repeated.ResolvedAtUtc);
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, JobName, ct);

    // The handler records the fire before it throws, so the count says the attempt really ran.
    private Task FailOneFireAsync(long slotId, int expectedFires, CancellationToken ct) =>
        AlertTestOps.FireSlotUntilAsync(
            Services,
            Runtime,
            slotId,
            () => RecurringPingHandler.TriggersFor(TestNamespace).Count,
            expectedFires,
            ct
        );

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain: null, ct);
}
