using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>Conformance for GetJobInput: the stored input reads back as enqueued, and a missing job reads null.</summary>
[ConformanceSpec(
    "job-payload-reads.input",
    "GetJobInput reads the stored input or null",
    Area = "Reads",
    Contract = "GetJobInput returns the payload and format a job was enqueued with, and null when no job row matches."
)]
public sealed class GetJobInputSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "GetJobInput returns the payload the job was enqueued with")]
    public async Task Input_readable_after_enqueue_equals_enqueued()
    {
        var ct = TestContext.Current.CancellationToken;

        var input = JobPayload.Json(new AddNumbers(4, 9));
        var enqueued = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", input), ct);

        var record = await Services.GetRequiredService<GetJobInputHandler>().ReadAsync(enqueued.JobId, ct);
        Assert.NotNull(record);
        Assert.Equal(input.Format.Id, record!.FormatId);
        Assert.Equal(input.Data.ToArray(), record.Input);

        var payload = (await Jobs.GetInputAsync(enqueued, ct))?.Input;
        Assert.NotNull(payload);
        Assert.Equal(input.Format.Id, payload!.Value.Format.Id);
        Assert.Equal(input.Data.ToArray(), payload.Value.Data.ToArray());
    }

    [Fact(DisplayName = "GetJobInput returns null when no job row matches the id")]
    public async Task Input_null_for_missing_job()
    {
        var ct = TestContext.Current.CancellationToken;

        var record = await Services.GetRequiredService<GetJobInputHandler>().ReadAsync(-987_654_321L, ct);
        Assert.Null(record);
    }
}
