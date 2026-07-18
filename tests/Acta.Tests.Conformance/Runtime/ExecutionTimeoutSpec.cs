using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for the per-attempt execution timeout. A handler that exceeds its
/// <c>ExecutionTimeout</c> has its cancellation token fired by the timeout source; the completion
/// records <c>ExecutionTimeout</c> distinctly from an external cancel and applies the retry budget
/// (here <c>MaxAttempts = 1</c>, so it lands terminal Failed).
/// </summary>
[ConformanceSpec(
    "execution-timeout.per-attempt",
    "A handler exceeding its timeout fails with the timeout reason",
    Area = "Execution",
    Contract = "A handler that exceeds its ExecutionTimeout has its token fired by the timeout source and the completion records ExecutionTimeout applying the retry budget.",
    Arrange = "A timeout-probe whose handler blocks on its token is registered with a 1s ExecutionTimeout and MaxAttempts 1.",
    Act = "The runtime claims and runs the job and the attempt exceeds its timeout.",
    Assert = "The timeout source fires the handler token and the job lands terminal Failed with the ExecutionTimeout reason, distinct from an external cancel."
)]
public abstract class ExecutionTimeoutSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "The timeout fires the handler token, the job lands Failed, and the reason is ExecutionTimeout distinct from external cancel"
    )]
    public async Task Handler_exceeding_timeout_fails_with_timeout_reason()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "timeout-probe", JobPayload.None), ct);

        // The handler blocks on its token forever; the 1s timeout cancels it and the run completes.
        await Runtime.RunOnceAsync(enqueued, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(snapshot);
        Assert.Equal(JobStatusCode.Failed, snapshot!.Status);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = events.Where(e => e.JobEventCode == JobEventCode.JobExecutionFinished).OrderByDescending(e => e.Id).First();
        Assert.Equal(JobEventReasonCode.JobExecutionTimeout, finished.JobEventReasonCode);
    }
}
