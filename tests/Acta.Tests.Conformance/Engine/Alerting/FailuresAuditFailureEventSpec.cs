using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Alerting;

/// <summary>
/// Conformance for the write gate at <c>EventRecordingCode.Failures</c>, which decides whether alerting can see
/// a job at all. Every failed attempt is written, the terminal one as <c>JobFailed</c>; a success is written when the
/// job's failure count says it answers a failure, or, under the probes' enabled alert profile, when a one-shot
/// succeeds past its first attempt, as after a restart; everything else is silent. The success is what lets an
/// incident opened at this level close: the projector resolves from it. SQLite once dropped every failure here
/// because its guard on the handler status was not three-valued, which is why every fact runs on all three providers.
/// </summary>
[ConformanceSpec(
    "alerts-projection.failures-audit-gate",
    "The failures-only level records every failure and the success that answers one",
    Area = "Alerts",
    Contract = "At Failures every failed attempt writes its event, the terminal one as JobFailed, and a success writes one when it answers a failure or a restart."
)]
public sealed class FailuresAuditFailureEventSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string JobName = "failures-audit-probe";
    private const string SlotName = "failures-audit-slot";

    // The probe's whole retry budget. Spending it is what turns the last throw terminal.
    private const int Budget = 6;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A one-shot that throws until its budget is gone writes a failed event per re-arm and one JobFailed, and its restarted success resolves"
    )]
    public async Task Terminal_throw_is_recorded_as_job_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: Budget);

        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), Budget, ct);

        var failures = await FailureEventsAsync(job.JobId, ct);
        Assert.Equal(Budget, failures.Count);
        Assert.All(failures.Take(Budget - 1), e => Assert.Equal(EventCode.JobExecutionFailed, e.EventCode));
        Assert.All(failures.Take(Budget - 1), e => Assert.Equal(JobStatusCode.Ready.Code, e.To));
        Assert.Equal(EventCode.JobFailed, failures[^1].EventCode);
        Assert.Equal(Budget, failures[^1].ExecutionNumber);

        await RunAlertsAsync(job.JobId, ct);
        var final = Assert.Single(await ReadAlertsAsync(NamespaceId, ct), a => a.Kind == AlertKindCode.FinalFailure);
        Assert.Equal(AlertSeverityCode.Error, final.SeverityCode);
        Assert.Equal(job.JobId, final.JobId);
        Assert.Null(final.ResolvedAtUtc);

        // Restart zeroes the failure count, so the success that follows is no recovery to this level's own rule,
        // but the alert profile records a one-shot's success past its first attempt, and that closes the incident.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, ct: ct)).Action);
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(job, ct))?.Status);
        Assert.Equal(Budget + 1, Assert.Single(await SuccessEventsAsync(job.JobId, ct)).ExecutionNumber);

        await RunAlertsAsync(job.JobId, ct);
        Assert.All(await ReadAlertsAsync(NamespaceId, ct), a => Assert.NotNull(a.ResolvedAtUtc));
    }

    [Fact(DisplayName = "A success at the failures-only recording level with no failure before it writes no event and opens no alert")]
    public async Task First_claim_success_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);

        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        // The status is asserted beside the silence, because an attempt that never ran would also write
        // nothing. This is the asymmetry a success rule applied too widely would break.
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(job, ct))?.Status);
        Assert.Empty(await GetEventsByJobId.Run(Services, job.JobId, ct));

        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    [Fact(
        DisplayName = "A one-shot that throws twice and succeeds inside its budget writes both failures and the recovery, which resolves"
    )]
    public async Task Retried_success_is_a_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 2);

        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, JobName, JobPayload.None), ct);
        await AlertTestOps.RunUntilAttemptsAsync(Runtime, job, () => FailuresAuditProbe.Attempts(TestNamespace), 3, ct);

        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(job, ct))?.Status);
        Assert.Equal(2, (await FailureEventsAsync(job.JobId, ct)).Count);
        var recovery = Assert.Single(await SuccessEventsAsync(job.JobId, ct));
        Assert.Equal(3, recovery.ExecutionNumber);

        await RunAlertsAsync(job.JobId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, resolved.Kind);
        Assert.NotNull(resolved.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A recurring slot that fails one fire raises, its next fire resolves, and the fires after that write nothing")]
    public async Task Recurring_slot_resolves_on_the_next_fire_and_then_stays_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditSlot.Reset(TestNamespace, failingFires: 1);
        var slotId = await AlertTestOps.RecurringSlotIdAsync(Services, TestNamespace, SlotName, ct);

        await FireAsync(slotId, expectedFires: 1, ct);
        var failure = Assert.Single(await FailureEventsAsync(slotId, ct));
        Assert.Equal(EventCode.JobExecutionFailed, failure.EventCode);
        Assert.Equal(JobStatusCode.Ready.Code, failure.To);

        await RunAlertsAsync(slotId, ct);
        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FirstFailure, raised.Kind);
        Assert.Null(raised.ResolvedAtUtc);

        await FireAsync(slotId, expectedFires: 2, ct);
        Assert.Single(await SuccessEventsAsync(slotId, ct));

        await RunAlertsAsync(slotId, ct);
        var resolved = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(raised.Id, resolved.Id);
        Assert.NotNull(resolved.ResolvedAtUtc);

        // The recovery reset the failure count, so healthy occurrences answer nothing and a slot that
        // fails once does not write a success a day forever after.
        await FireAsync(slotId, expectedFires: 3, ct);
        await FireAsync(slotId, expectedFires: 4, ct);
        Assert.Single(await SuccessEventsAsync(slotId, ct));
    }

    private Task FireAsync(long slotId, int expectedFires, CancellationToken ct) =>
        AlertTestOps.FireSlotUntilAsync(Services, Runtime, slotId, () => FailuresAuditSlot.Fires(TestNamespace), expectedFires, ct);

    private async Task<List<JobEventRecord>> FailureEventsAsync(long jobId, CancellationToken ct) =>
        (await GetEventsByJobId.Run(Services, jobId, ct))
            .Where(e => e.EventCode is EventCode.JobExecutionFailed or EventCode.JobFailed)
            .OrderBy(e => e.Id)
            .ToList();

    private async Task<List<JobEventRecord>> SuccessEventsAsync(long jobId, CancellationToken ct) =>
        (await GetEventsByJobId.Run(Services, jobId, ct)).Where(e => e.EventCode == EventCode.JobExecutionSucceeded).ToList();

    private Task RunAlertsAsync(long cursorOwnerJobId, CancellationToken ct) =>
        AlertTestOps.RunAlertsJobAsync(Services, TestNamespace, NamespaceId, cursorOwnerJobId, options: null, drain: null, ct);
}
