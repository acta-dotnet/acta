using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Execution;

/// <summary>Conformance for <c>complete_job_step</c>, which records an invoked step's outcome under the version its start returned.</summary>
[ConformanceSpec(
    "complete-job-step.decision",
    "Completing a step stores a success or fails an at-most-once step",
    Area = "Execution",
    Contract = "A step completion stores a success, fails the step on an at-most-once body's failure, and writes nothing on a stale version."
)]
public sealed class CompleteJobStepSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string StepName = "ship";
    private const string Reason = "carrier offline";
    private static readonly byte[] Result = Encoding.UTF8.GetBytes("{\"tracking\":\"x1\"}");

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A success stores the result and marks the step Succeeded")]
    public async Task Success_stores_the_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var start = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);

        var done = await SucceedAsync(job, start.Version, ct);

        Assert.Equal(new CompleteJobStepResponse(CompleteJobStepOutcomeCode.Succeeded), done);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Succeeded, step.Status);
        Assert.Equal(JobPayloadFormat.Json.Id, step.ResultFormatId);
        Assert.Equal(Result, step.Result);
        Assert.Null(step.FailureMessage);
        Assert.Equal(start.Version + 1, step.Version);
    }

    [Fact(DisplayName = "An at-most-once body's failure ends the step Failed with its reason")]
    public async Task Failure_fails_the_step()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var start = await Execution.StartJobStepAsync(job, StepName, atMostOnce: true, ct);

        var failed = await FailAsync(job, start.Version, ct);

        Assert.Equal(new CompleteJobStepResponse(CompleteJobStepOutcomeCode.Failed), failed);
        var step = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Failed, step.Status);
        Assert.Equal(Reason, step.FailureMessage);
        Assert.Null(step.Result);
        Assert.Equal(start.Version + 1, step.Version);
    }

    [Fact(DisplayName = "A success or a failure carrying a stale version reports StaleVersion and writes nothing")]
    public async Task Stale_version_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await EnqueueAsync(ct);
        var stale = await Execution.StartJobStepAsync(job, StepName, atMostOnce: false, ct);
        // A concurrent invocation of the same step records first and moves the version on.
        await SucceedAsync(job, stale.Version, ct);
        var before = await ReadStepAsync(job, ct);

        var success = await SucceedAsync(job, stale.Version, ct);
        var failure = await FailAsync(job, stale.Version, ct);

        Assert.Equal(new CompleteJobStepResponse(CompleteJobStepOutcomeCode.StaleVersion), success);
        Assert.Equal(new CompleteJobStepResponse(CompleteJobStepOutcomeCode.StaleVersion), failure);
        var after = await ReadStepAsync(job, ct);
        Assert.Equal(StepStatusCode.Succeeded, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(Result, after.Result);
        Assert.Null(after.FailureMessage);
    }

    // A job staged Executing, as a claim leaves it, so the step writes pass the execution fence.
    private async Task<ExecutionFence> EnqueueAsync(CancellationToken ct) =>
        await ExecutionFenceStaging.ExecutingAsync(
            Db,
            (await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))), ct)).JobId,
            ct
        );

    private Task<CompleteJobStepResponse> SucceedAsync(ExecutionFence job, int version, CancellationToken ct) =>
        Execution.CompleteJobStepAsync(
            new CompleteJobStepCommand(job, StepName, true, JobPayloadFormat.Json.Id, Result, null, version),
            ct
        );

    private Task<CompleteJobStepResponse> FailAsync(ExecutionFence job, int version, CancellationToken ct) =>
        Execution.CompleteJobStepAsync(
            new CompleteJobStepCommand(job, StepName, Succeeded: false, ResultFormatId: 0, Result: null, Reason, version),
            ct
        );

    private async Task<StepEntity> ReadStepAsync(ExecutionFence job, CancellationToken ct)
    {
        var jobId = job.JobId;
        return Assert.Single(await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.StepName == StepName).ToListAsync(ct));
    }
}
