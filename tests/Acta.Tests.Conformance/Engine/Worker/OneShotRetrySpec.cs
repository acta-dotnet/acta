using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// Conformance for one-shot retry/backoff. A failed one-shot (unhandled exception) re-arms to Ready
/// with the failure budget incremented while attempts remain, then lands terminal Failed once
/// <c>MaxAttempts</c> is reached. Every failed attempt records execution status Failed, the retried ones
/// too, because each charges the budget. <c>MaxAttempts</c> is the one-off retry budget only; a recurring
/// slot never terminalizes on its consecutive-failure count. <c>retry-probe</c> uses a zero initial backoff
/// so each tick re-claims immediately.
/// </summary>
[ConformanceSpec(
    "one-shot-retry.budget",
    "A failed one-shot retries to Ready until MaxAttempts then Fails",
    Area = "Execution",
    Contract = "A failed one-shot re-arms to Ready incrementing failure_count while attempts remain and lands terminal Failed once MaxAttempts is reached."
)]
public sealed class OneShotRetrySpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(
        DisplayName = "In-budget failures re-arm to Ready bumping failure_count and an exhausted budget lands Failed with the failure reason preserved"
    )]
    public async Task Failed_one_shot_retries_until_max_attempts_then_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        RetryProbe.Reset(TestNamespace);

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "retry-probe", JobPayload.None), ct);

        // Attempts 1 and 2 are in budget (MaxAttempts = 3): each re-arms to Ready, bumping failure_count.
        await Runtime.RunOnceAsync(enqueued, ct);
        var after1 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Ready, after1!.Status);
        Assert.Equal(1, after1.FailureCount);

        await Runtime.RunOnceAsync(enqueued, ct);
        var after2 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Ready, after2!.Status);
        Assert.Equal(2, after2.FailureCount);

        // Attempt 3 exhausts the budget: terminal Failed, keeping the failure reason.
        await Runtime.RunOnceAsync(enqueued, ct);
        var after3 = await Jobs.GetAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Failed, after3!.Status);
        Assert.Equal(3, after3.FailureCount);

        var events = await GetEventsByJobId.Run(Services, enqueued.JobId, ct);
        var failed = Assert.Single(events, e => e.EventCode == EventCode.JobFailed);
        Assert.Equal(EventReason.UnhandledException, failed.Reason);
        Assert.Equal(3, failed.ExecutionNumber);

        Assert.Equal(3, RetryProbe.Attempts(TestNamespace));
    }

    [Fact(DisplayName = "A one-shot that throws twice and then succeeds records both retried attempts as failed at Audit recording")]
    public async Task Retried_failures_record_failed_and_the_success_records_succeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        FlakyRecoverProbe.Reset(TestNamespace, failingAttempts: 2);

        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "flaky-recover", JobPayload.None), ct);
        await Runtime.RunOnceAsync(enqueued, ct);
        await Runtime.RunOnceAsync(enqueued, ct);
        await Runtime.RunOnceAsync(enqueued, ct);
        Assert.Equal(JobStatusCode.Succeeded, (await Jobs.GetStatusAsync(enqueued, ct))?.Status);

        var attempts = (await GetEventsByJobId.Run(Services, enqueued.JobId, ct))
            .Where(e => e.EventCode is EventCode.JobExecutionFailed or EventCode.JobExecutionSucceeded)
            .OrderBy(e => e.Id)
            .Select(e => (e.EventCode, e.To))
            .ToList();
        (EventCode, string?)[] expected =
        [
            (EventCode.JobExecutionFailed, JobStatusCode.Ready.Code),
            (EventCode.JobExecutionFailed, JobStatusCode.Ready.Code),
            (EventCode.JobExecutionSucceeded, null),
        ];
        Assert.Equal(expected, attempts);
    }
}
