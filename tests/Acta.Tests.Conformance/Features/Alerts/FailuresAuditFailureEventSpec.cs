using Acta.Relational.Entities;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Execution;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Alerts;

/// <summary>
/// Conformance for the write gate at <c>AuditLevel.Failures</c>, which decides whether alerting can see
/// a job at all. A failure carrying no reschedule is written, a success is written only when the job's
/// newest finished event is not a success, and everything else is silent. The second rule is what lets
/// an incident opened at this level close: the projector resolves from a success event, and the one
/// success that follows a recorded failure is the one that is written. SQLite once dropped every
/// failure here because its guard on the handler status was not three-valued, which is why every fact
/// runs on all three providers.
/// </summary>
[ConformanceSpec(
    "alerts-projection.failures-audit-gate",
    "The failures-only audit level records a failure and the success that answers it",
    Area = "Alerts",
    Contract = "At AuditLevel.Failures a failure carrying no reschedule writes its finished event, and a success writes one only when the newest finished event is a failure.",
    Arrange = "A one-shot probe and a recurring slot, both declared at AuditLevel.Failures with the default OnFailure profile, in a namespace holding no alerts.",
    Act = "The probe fails out of budget and is restarted to success, succeeds first time, and succeeds after retries, and the slot fails one fire then succeeds three.",
    Assert = "The terminal failure and its answering success write once each and the incident resolves, other successes write nothing, and the slot writes two events only."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.ResolveJobAlertsAsync))]
public abstract class FailuresAuditFailureEventSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string JobName = "failures-audit-probe";
    private const string SlotName = "failures-audit-slot";

    // The probe's whole retry budget. Spending it is what turns the last throw terminal, and a terminal
    // failure is the one failure shape this level records for a one-shot.
    private const int Budget = 6;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A one-shot that throws until its budget is gone writes one finished event and opens a FinalFailure, and its restarted success closes it"
    )]
    public async Task Terminal_throw_is_recorded_and_the_restarted_success_resolves()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: Budget);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), Budget, ct);

        // Exactly one, not one per attempt: the five in-budget re-arms each carry a reschedule, which
        // this level excludes, so the terminal attempt is the only one that writes. The count is what
        // pins that exclusion, and the row itself is what SQLite used to drop entirely.
        var failed = Assert.Single(await FinishedEventsAsync(job.JobId, ct));
        Assert.Equal(ExecutionStatusCode.Failed, failed.ExecutionStatus);
        Assert.Equal(JobStatusCode.Failed, failed.ToStatus);

        await RunAlertsAsync(job.JobId, ct);
        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FinalFailure, raised.Kind);
        Assert.Equal(AlertSeverityCode.Error, raised.SeverityCode);
        Assert.Equal(job.JobId, raised.JobId);
        Assert.Null(raised.ResolvedAtUtc);

        // Restart writes nothing at this level and zeroes the failure count, so the failure event is the
        // only evidence left that the success has something to answer.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, ct: ct)).Action);
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(job, ct));

        var finished = await FinishedEventsAsync(job.JobId, ct);
        Assert.Equal(2, finished.Count);
        Assert.Equal(ExecutionStatusCode.Succeeded, finished[^1].ExecutionStatus);

        await RunAlertsAsync(job.JobId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(raised.Id, resolved.Id);
        Assert.NotNull(resolved.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A success at the failures-only audit level with no failure before it writes no finished event and opens no alert")]
    public async Task First_claim_success_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        // The status is asserted beside the silence, because an attempt that never ran would also write
        // nothing. This is the asymmetry a success rule applied too widely would break.
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(job, ct));
        Assert.Empty(await FinishedEventsAsync(job.JobId, ct));

        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    [Fact(DisplayName = "A one-shot that throws twice and succeeds inside its budget writes nothing at all")]
    public async Task Retried_success_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 2);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 3, ct);

        // The two throws were re-arms, which this level drops, so the success has no failure to answer.
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(job, ct));
        Assert.Empty(await FinishedEventsAsync(job.JobId, ct));

        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    [Fact(DisplayName = "A recurring slot that fails one fire raises, its next fire resolves, and the fires after that write nothing")]
    public async Task Recurring_slot_resolves_on_the_next_fire_and_then_stays_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditSlot.Reset(TestNamespace, failingFires: 1);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, SlotName, ct);

        await FireAsync(slotId, expectedFires: 1, ct);
        var failure = Assert.Single(await FinishedEventsAsync(slotId, ct));
        Assert.Equal(JobStatusCode.Ready, failure.ToStatus);
        Assert.Equal(ExecutionStatusCode.Failed, failure.ExecutionStatus);

        await RunAlertsAsync(slotId, ct);
        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Null(raised.ResolvedAtUtc);

        await FireAsync(slotId, expectedFires: 2, ct);
        var finished = await FinishedEventsAsync(slotId, ct);
        Assert.Equal(2, finished.Count);
        Assert.Equal(ExecutionStatusCode.Succeeded, finished[^1].ExecutionStatus);

        await RunAlertsAsync(slotId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(raised.Id, resolved.Id);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // Healthy occurrences answer nothing, so a slot that fails once does not write a success a day
        // forever after.
        await FireAsync(slotId, expectedFires: 3, ct);
        await FireAsync(slotId, expectedFires: 4, ct);
        Assert.Equal(2, (await FinishedEventsAsync(slotId, ct)).Count);
    }

    private Task FireAsync(long slotId, int expectedFires, CancellationToken ct) =>
        AlertTestOps.FireSlotUntilAsync(Services, Runtime, slotId, () => FailuresAuditSlot.Fires(TestNamespace), expectedFires, ct);

    private async Task<List<JobEventRecord>> FinishedEventsAsync(long jobId, CancellationToken ct) =>
        (await GetEventsByJobId.Run(Services, jobId, ct))
            .Where(e => e.EventCode == EventCode.JobExecutionFinished)
            .OrderBy(e => e.Id)
            .ToList();

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain: null, ct);
}
