using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for the per-attempt execution timeout with retry budget remaining. A timeout with budget
/// left re-arms the job to Ready (incrementing <c>failure_count</c>); once <c>MaxAttempts</c> is
/// exhausted the job lands terminal Failed. This proves the re-arm path and failure_count progression
/// that <c>ExecutionTimeoutSpec</c> (MaxAttempts=1, terminal only) does not cover.
/// </summary>
[ConformanceSpec(
    "timeout.retry-budget",
    "A timeout within budget re-arms to Ready; exhausted budget lands Failed",
    Area = "Execution",
    Contract = "A per-attempt timeout re-arms the job to Ready incrementing failure_count while budget remains and lands terminal Failed once MaxAttempts is exhausted."
)]
public sealed class TimeoutRetryBudgetSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Timeout re-arms within budget bumping failure_count, then terminates Failed once MaxAttempts is exhausted")]
    public async Task Timeout_rearms_within_budget_then_terminates_at_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "timeout-budget-probe", JobPayload.None), ct);

        // Attempt 1: handler blocks until the 1s timeout fires.
        // In budget (1 < MaxAttempts=2): job re-arms to Ready, failure_count 0→1.
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var after1 = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(after1);
        Assert.Equal(JobStatusCode.Ready, after1!.Status);
        Assert.Equal(1, after1.FailureCount);
        Assert.NotNull(after1.NextRunAtUtc);

        var events1 = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var failed1 = Assert.Single(events1, e => e.EventCode == EventCode.JobExecutionFailed);
        Assert.Equal(EventReason.ExecutionTimeout, failed1.Reason);
        Assert.Equal(JobStatusCode.Ready.Code, failed1.To);

        // Attempt 2: claim+run again (zero backoff, so the job is immediately claimable).
        // Budget exhausted (2 >= MaxAttempts=2): job lands terminal Failed, failure_count 1→2.
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(SpecWaits.Gate, ct);

        var after2 = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(after2);
        Assert.Equal(JobStatusCode.Failed, after2!.Status);
        Assert.Equal(2, after2.FailureCount);

        var events2 = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var failed2 = Assert.Single(events2, e => e.EventCode == EventCode.JobFailed);
        Assert.Equal(EventReason.ExecutionTimeout, failed2.Reason);
    }
}
