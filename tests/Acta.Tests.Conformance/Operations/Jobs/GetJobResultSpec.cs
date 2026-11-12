using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for the three reads of a job's result, none of which waits. They differ in how they say there is no
/// result, which is the same answer for a job that has stored none and for a lookup that matches no job: the raw read
/// answers <c>null</c>, the typed Try read answers not found, and the typed Get read throws, saying which it was.
/// </summary>
[ConformanceSpec(
    "get-job-result.non-blocking",
    "GetJobResult never waits: raw answers null, Try answers not found, Get throws",
    Area = "Results",
    Contract = "No result read waits, and with no result the raw read answers null, the typed TryGetResultAsync answers not found, and the typed GetResultAsync throws."
)]
[Deploys("add-numbers")]
public sealed class GetJobResultSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobResultSpec>>
{
    [Fact(DisplayName = "Before a result exists the raw read answers null and the typed Try read not found, neither waiting")]
    public async Task Result_is_null_before_the_job_produces_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var enqueued = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );

        Assert.Null(await Jobs.GetResultAsync(enqueued, ct));
        Assert.Equal((false, null), await Jobs.TryGetResultAsync<AddNumbersResult>(enqueued, ct));
    }

    [Fact(DisplayName = "The typed Get read throws, naming the job and its status, when the job has stored no result")]
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

    [Fact(
        DisplayName = "For a lookup that matches no job the raw read answers null, the typed Try read not found, and the typed Get read throws, saying no job matches"
    )]
    public async Task A_lookup_that_matches_no_job_has_no_result()
    {
        var ct = TestContext.Current.CancellationToken;
        var nobody = JobLookup.ByDeduplicationKey(TestNamespace, Unique("no-such-job"));

        Assert.Null(await Jobs.GetResultAsync(nobody, ct));
        Assert.Equal((false, null), await Jobs.TryGetResultAsync<AddNumbersResult>(nobody, ct));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.GetResultAsync<AddNumbersResult>(nobody, ct));
        Assert.StartsWith("No job matches", none.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "After a successful run the raw read answers the stored payload and both typed reads the deserialized result")]
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
        Assert.Equal((true, typed), await Jobs.TryGetResultAsync<AddNumbersResult>(enqueued, ct));

        var raw = (await Jobs.GetResultAsync(enqueued, ct))?.Result;
        Assert.NotNull(raw);
        Assert.Equal(JobPayloadFormat.Json.Id, raw!.Value.Format.Id);
    }
}
