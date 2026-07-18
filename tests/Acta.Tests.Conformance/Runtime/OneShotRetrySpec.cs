using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for one-shot retry/backoff. A failed one-shot (unhandled exception) re-arms to Ready
/// with the failure budget incremented while attempts remain, then lands terminal Failed once
/// <c>MaxAttempts</c> is reached - mirroring the recurring failure-budget path for non-recurring jobs.
/// <c>retry-probe</c> uses a zero initial backoff so each tick re-claims immediately.
/// </summary>
[ConformanceSpec(
    "one-shot-retry.budget",
    "A failed one-shot retries to Ready until MaxAttempts then Fails",
    Area = "Execution",
    Contract = "A failed one-shot re-arms to Ready incrementing failure_count while attempts remain and lands terminal Failed once MaxAttempts is reached.",
    Arrange = "A retry-probe that always throws is registered with MaxAttempts 3 and zero backoff.",
    Act = "The runtime claims and runs the job three times.",
    Assert = "Attempts 1 and 2 re-arm Ready bumping failure_count and attempt 3 lands terminal Failed with UnhandledException."
)]
public abstract class OneShotRetrySpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(
        DisplayName = "In-budget failures re-arm to Ready bumping failure_count and an exhausted budget lands Failed with the failure reason preserved"
    )]
    public async Task Failed_one_shot_retries_until_max_attempts_then_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var enqueued = await Jobs.EnqueueAsync(new JobEnqueueRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        // Attempts 1 and 2 are in budget (MaxAttempts = 3): each re-arms to Ready, bumping failure_count.
        await Runtime.RunOnceAsync(enqueued, ct);
        var after1 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Ready, after1!.Status);
        Assert.Equal((short)1, after1.FailureCount);

        await Runtime.RunOnceAsync(enqueued, ct);
        var after2 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Ready, after2!.Status);
        Assert.Equal((short)2, after2.FailureCount);

        // Attempt 3 exhausts the budget: terminal Failed, keeping the failure reason.
        await Runtime.RunOnceAsync(enqueued, ct);
        var after3 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Failed, after3!.Status);
        Assert.Equal((short)3, after3.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var finished = events.Where(e => e.JobEventCode == JobEventCode.JobExecutionFinished).OrderByDescending(e => e.Id).First();
        Assert.Equal(JobEventReasonCode.JobUnhandledException, finished.JobEventReasonCode);

        Assert.Equal(3, RetryProbe.Attempts(TestNamespace));
    }
}
