using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for a body that finished and whose result cannot be recorded: a retry would run the finished body again,
/// so the job fails for good on that attempt, and an at-most-once step is not run by a restart either.
/// </summary>
[ConformanceSpec(
    "results.not-recorded",
    "A result that cannot be recorded never runs its body again",
    Area = "Steps",
    Contract = "A finished body whose result cannot be recorded fails its job for good instead of running again, and an at-most-once step stays unrun through a restart."
)]
public sealed class ResultNotRecordedSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.MaxInlinePayloadBytes = 1024);
    }

    [Theory(DisplayName = "A step whose result is null or over the cap fails the job for good with its body run once")]
    [InlineData("job-step-null-result", "returned null")]
    [InlineData("job-step-oversize-result", "MaxInlinePayloadBytes")]
    public async Task Unrecorded_step_result_fails_the_job_without_a_rerun(string jobName, string why)
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, jobName, JobPayload.None), ct);

        // The default budget has attempts left: a retryable failure would re-arm the job and run the body again.
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Equal(EventReason.NonRetryableException, EventDetail(failed, "reason"));
        Assert.Contains("its result was not recorded", failed.EventMessage, StringComparison.Ordinal);
        Assert.Contains(why, failed.EventMessage, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An at-most-once step whose result cannot be recorded never runs again, a restart of its job included")]
    public async Task Unrecorded_at_most_once_result_survives_a_restart_unrun()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "job-step-null-result-at-most-once", JobPayload.None),
            ct
        );
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        // A restart deletes a failed at-most-once step so it can run again. This body finished, so its step was left
        // started, and the restarted job finds it interrupted.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(enqueued, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        Assert.Equal(1, JobStepProbes.BodyInvocations[enqueued.JobId]);
        var step = Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == enqueued.JobId).ToListAsync(ct));
        Assert.Equal(StepStatusCode.Interrupted, step.Status);
    }

    [Fact(DisplayName = "A handler whose result cannot be serialized fails the job for good on that attempt")]
    public async Task Unserializable_handler_result_fails_the_job_without_a_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "unserializable-result", JobPayload.None), ct);

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(enqueued, ct));

        var job = await ReadJobAsync(enqueued.JobId, ct);
        Assert.Equal(JobStatusCode.Failed, job.Status);
        Assert.Equal(0, job.FailureCount);
        var failed = await ReadLatestEventAsync(enqueued.JobId, EventCode.JobFailed, ct);
        Assert.Contains("could not be serialized", failed.EventMessage, StringComparison.Ordinal);
    }
}
