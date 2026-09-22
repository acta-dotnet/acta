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
/// a job at all. SQLite dropped every failure here until the guard on the handler status was made
/// three-valued: the parameter is NULL for an ordinary failure, <c>NULL IN (...)</c> is NULL, and
/// <c>NOT NULL</c> is NULL, so the branch wrote nothing and the job never alerted. PostgreSQL and SQL
/// Server derive a boolean from the same NULL check first, which is why only one engine was wrong and
/// why this runs on all three.
/// </summary>
[ConformanceSpec(
    "alerts-projection.failures-audit-gate",
    "The failures-only audit level records a failure and stays silent otherwise",
    Area = "Alerts",
    Contract = "At AuditLevel.Failures a failed attempt carrying no reschedule writes its finished event and alerts, while a success writes nothing.",
    Arrange = "A probe declared at AuditLevel.Failures with the default OnFailure profile, in a namespace holding no alerts.",
    Act = "The probe is run to a terminal failure, and separately run to a clean success.",
    Assert = "The terminal failure writes one finished event and opens a FinalFailure, and the success writes no event and opens nothing."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
[CoversStoreMethod(typeof(IAlertStore), nameof(IAlertStore.GetAlertableEventsAsync))]
public abstract class FailuresAuditFailureEventSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string JobName = "failures-audit-probe";

    // The probe's whole retry budget. Spending it is what turns the last throw terminal, and a terminal
    // failure is the one shape this level records for a one-shot.
    private const int Budget = 6;

    private int NamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(DisplayName = "A one-shot that throws until its budget is gone writes one finished event and opens a FinalFailure")]
    public async Task Terminal_throw_at_failures_audit_level_is_recorded_and_alerts()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: Budget);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => FailuresAuditProbe.Attempts(TestNamespace), Budget, ct);

        // Exactly one, not one per attempt: the five in-budget re-arms each carry a reschedule, which
        // this level excludes, so the terminal attempt is the only one that writes. The count is what
        // pins that exclusion, and the row itself is what SQLite used to drop entirely.
        var finished = (await GetEventsByJobId.Run(Services, job.JobId, ct))
            .Where(e => e.EventCode == EventCode.JobExecutionFinished)
            .ToList();
        Assert.Single(finished);
        Assert.Equal(ExecutionStatusCode.Failed, finished[0].ExecutionStatus);
        Assert.Equal(JobStatusCode.Failed, finished[0].ToStatus);

        await RunAlertsAsync(job.JobId, ct);

        var raised = Assert.Single(await ReadAlertsAsync(NamespaceId, ct));
        Assert.Equal(AlertKindCode.FinalFailure, raised.Kind);
        Assert.Equal(AlertSeverityCode.Error, raised.SeverityCode);
        Assert.Equal(job.JobId, raised.JobId);
        Assert.Null(raised.ResolvedAtUtc);
    }

    [Fact(DisplayName = "A success at the failures-only audit level writes no finished event and opens no alert")]
    public async Task Success_at_failures_audit_level_is_silent()
    {
        var ct = TestContext.Current.CancellationToken;
        FailuresAuditProbe.Reset(TestNamespace, failingAttempts: 0);

        var job = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, JobName, JobPayload.None), ct);
        await RunUntilAttemptsAsync(job, () => FailuresAuditProbe.Attempts(TestNamespace), 1, ct);

        // The status is asserted beside the silence, because an attempt that never ran would also write
        // nothing. This is the asymmetry a fix applied too widely would break.
        Assert.Equal(JobStatusCode.Succeeded, await Jobs.GetStatusAsync(job, ct));
        Assert.DoesNotContain(await GetEventsByJobId.Run(Services, job.JobId, ct), e => e.EventCode == EventCode.JobExecutionFinished);

        await RunAlertsAsync(job.JobId, ct);
        Assert.Empty(await ReadAlertsAsync(NamespaceId, ct));
    }

    // RunOnceAsync can no-op when a claim is lost to provider timing, so the loop is driven by the
    // probe's own attempt count rather than by its return.
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
