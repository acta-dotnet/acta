using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for GetJobInput: the stored input reads back as enqueued or amended, raw or as its type. Where the raw read
/// answers null, for a missing job and a no-input job, the typed read throws, and it refuses a registered job's input read
/// as another type.
/// </summary>
[ConformanceSpec(
    "job-payload-reads.input",
    "GetJobInput reads the stored input, raw or as its type",
    Area = "Reads",
    Contract = "GetJobInput returns a job's stored payload or null, and the typed read decodes it as its type or throws for no job, a no-input job, or another input type."
)]
[Deploys("add-numbers", "cancellable")]
public sealed class GetJobInputSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobInputSpec>>
{
    [Fact(DisplayName = "GetJobInput returns the payload the job was enqueued with")]
    public async Task Input_readable_after_enqueue_equals_enqueued()
    {
        var ct = TestContext.Current.CancellationToken;

        var input = JobPayload.Json(new AddNumbers(4, 9));
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", input), ct);

        var record = await Services.GetRequiredService<GetJobInputHandler>().ReadAsync(enqueued.JobRef, ct);
        Assert.NotNull(record);
        Assert.Equal((TestNamespace, "add-numbers"), (record!.JobNamespace, record.JobName));
        Assert.Equal(input.Format.Id, record.FormatId);
        Assert.Equal(input.Data.ToArray(), record.Input);

        var payload = (await Jobs.GetInputAsync(enqueued.JobRef, ct))?.Input;
        Assert.NotNull(payload);
        Assert.Equal(input.Format.Id, payload!.Value.Format.Id);
        Assert.Equal(input.Data.ToArray(), payload.Value.Data.ToArray());
    }

    [Fact(DisplayName = "GetJobInput returns null when no job row matches the id")]
    public async Task Input_null_for_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var missing = JobLookup.ById(long.MaxValue);

        Assert.Null(await Services.GetRequiredService<GetJobInputHandler>().ReadAsync(missing, ct));
        Assert.Null(await Jobs.GetInputAsync(missing, ct));
    }

    [Fact(DisplayName = "The typed read answers the input as its type, as enqueued and as amended")]
    public async Task Typed_read_answers_the_input_as_its_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(new AddNumbers(4, 9), ct: ct);

        Assert.Equal(new AddNumbers(4, 9), await Jobs.GetInputAsync<AddNumbers>(handle.JobRef, ct));

        var amended = await Jobs.UpdateInputAsync(handle.JobRef, JobPayload.Json(new AddNumbers(7, 8)), "corrected operands", ct: ct);
        Assert.Equal(ControlAction.Applied, amended.Action);
        Assert.Equal(new AddNumbers(7, 8), await Jobs.GetInputAsync<AddNumbers>(handle.JobRef, ct));
    }

    [Fact(DisplayName = "The typed read throws, naming the lookup, when no job matches")]
    public async Task Typed_read_throws_when_no_job_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Unique("no-such-job");

        var none = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Jobs.GetInputAsync<AddNumbers>(JobLookup.ByDeduplicationKey(TestNamespace, key), ct)
        );
        Assert.StartsWith("No job matches", none.Message, StringComparison.Ordinal);
        Assert.Contains(key, none.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The typed read throws, naming the job, for a job that carries no input, which the raw read answers as null")]
    public async Task Typed_read_throws_for_a_no_input_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(TestJobsManifest.Cancellable(), ct: ct);

        Assert.Null(await Jobs.GetInputAsync(handle.JobRef, ct));
        var none = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.GetInputAsync<AddNumbers>(handle.JobRef, ct));
        Assert.Contains($"'{TestNamespace}/cancellable' carries no input", none.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The typed read refuses a registered job's input read as another type, naming both types")]
    public async Task Typed_read_refuses_another_input_type()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await Jobs.EnqueueAsync(new AddNumbers(4, 9), ct: ct);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await Jobs.GetInputAsync<Echo>(handle.JobRef, ct));
        Assert.Contains($"takes '{typeof(AddNumbers).FullName}', not '{typeof(Echo).FullName}'", refused.Message, StringComparison.Ordinal);
    }
}
