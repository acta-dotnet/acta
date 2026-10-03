using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Runtime;

/// <summary>
/// Conformance for the non-blocking <c>IJobs.GetJobResult</c> read. Before the job produces a result the
/// fetch is <c>null</c> (it does not wait); after a successful run the typed overload deserializes the
/// handler's result and the raw overload returns the stored payload.
/// </summary>
[ConformanceSpec(
    "get-job-result.non-blocking",
    "GetJobResult returns null before completion and the typed result after",
    Area = "Results",
    Contract = "GetJobResult is a non-blocking read that returns null before the job produces a result and the typed and raw payload after a successful run."
)]
public sealed class GetJobResultSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "Returns null before a result exists without blocking")]
    public async Task Result_is_null_before_the_job_produces_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        Assert.Null(await Jobs.GetResultAsync(enqueued, ct));
        Assert.Null(await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct));
    }

    [Fact(DisplayName = "The typed result deserializes and the raw payload is returned after completion")]
    public async Task Typed_and_raw_result_are_available_after_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        await Runtime.RunOnceAsync(enqueued, ct);

        var typed = await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct);
        Assert.NotNull(typed);
        Assert.Equal(5, typed!.Sum);

        var raw = await Jobs.GetResultAsync(enqueued, ct);
        Assert.NotNull(raw);
        Assert.Equal(JobPayloadFormat.Json.Id, raw!.Value.Format.Id);
    }
}
