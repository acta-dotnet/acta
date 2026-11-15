using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for reading a step's result from outside the job. The result a succeeded step recorded reads back as its
/// type under any casing of its name, and default stands for a step with no result, a step not run, and no job alike.
/// </summary>
[ConformanceSpec(
    "get-job-step-result-or-default.reads",
    "GetJobStepResultOrDefault reads a step's result from outside the job",
    Area = "Steps",
    Contract = "A succeeded step's result reads back outside the job as its type, default for a step without one, a step not run, or no job."
)]
[Deploys("job-step-basic", "job-step-void")]
public sealed class GetJobStepResultOrDefaultSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobStepResultOrDefaultSpec>>
{
    [Fact(DisplayName = "A succeeded step's result reads back as its type under any casing of its name, and default otherwise")]
    public async Task A_succeeded_steps_result_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var typed = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-basic", JobPayload.None), ct);
        var untyped = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-step-void", JobPayload.None), ct);

        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<string>(typed.JobRef, "compute", ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(typed, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(untyped, ct));

        Assert.Equal("ok", await Jobs.GetStepResultOrDefaultAsync<string>(typed.JobRef, "compute", ct));
        Assert.Equal("ok", await Jobs.GetStepResultOrDefaultAsync<string>(typed.JobRef, " Compute ", ct));
        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<string>(untyped.JobRef, "side-effect", ct));
        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<string>(typed.JobRef, "never-ran", ct));
        Assert.Null(await Jobs.GetStepResultOrDefaultAsync<string>(JobRef.New(), "compute", ct));
    }
}
