using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for the non-blocking <c>IJobs.GetResult</c> read. Before the job produces a result the
/// fetch is <c>null</c> (it does not wait); after a successful run the typed overload deserializes the
/// handler's result and the raw overload returns the stored payload.
/// </summary>
[ConformanceSpec(
    "get-job-result.non-blocking",
    "GetJobResult returns null before completion and the typed result after",
    Area = "Results",
    Contract = "GetJobResult is a non-blocking read that returns null before the job produces a result and the typed and raw payload after a successful run."
)]
[Deploys("add-numbers")]
public sealed class GetJobResultSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobResultSpec>>
{
    [Fact(DisplayName = "Returns null before a result exists without blocking")]
    public async Task Result_is_null_before_the_job_produces_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        Assert.Null((await Jobs.GetResultAsync(enqueued, ct))?.Result);
        Assert.Equal((false, null), await Jobs.TryGetResultAsync<AddNumbersResult>(enqueued, ct));
    }

    [Fact(DisplayName = "A read that expects a result throws, naming the job and its status, when the job has stored none")]
    public async Task A_read_that_expects_a_result_throws_when_there_is_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(new AddNumbers(2, 3), ct: ct);

        var none = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct)
        );
        Assert.Contains(enqueued.JobRef.ToString(), none.Message, StringComparison.Ordinal);
        Assert.Contains("is Ready and has stored no result", none.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await enqueued.GetResultAsync<AddNumbersResult>(ct));
    }

    [Fact(DisplayName = "A lookup that matches no job reads as not found, and a read that expects a result says no job matches")]
    public async Task A_lookup_that_matches_no_job_has_no_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var nobody = JobLookup.ByDeduplicationKey(TestNamespace, Unique("no-such-job"));

        Assert.Equal((false, null), await Jobs.TryGetResultAsync<AddNumbersResult>(nobody, ct));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.GetResultAsync<AddNumbersResult>(nobody, ct));
        Assert.StartsWith("No job matches", none.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The typed result deserializes and the raw payload is returned after completion")]
    public async Task Typed_and_raw_result_are_available_after_completion()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        await Runtime.RunOnceAsync(enqueued, ct);

        var typed = await Jobs.GetResultAsync<AddNumbersResult>(enqueued, ct);
        Assert.Equal(5, typed.Sum);

        var raw = (await Jobs.GetResultAsync(enqueued, ct))?.Result;
        Assert.NotNull(raw);
        Assert.Equal(JobPayloadFormat.Json.Id, raw!.Value.Format.Id);
    }
}
