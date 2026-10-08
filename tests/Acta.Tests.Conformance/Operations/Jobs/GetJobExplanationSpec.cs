using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobExplanation</c>: the composite explain read that returns three result sets
/// (header snapshot + leasing worker + latest reason, step rows, checkpoint rows) for a known job id,
/// and <c>null</c> for an unknown id. Exercises the multi-result-set mapping and the absent-row sentinel.
/// </summary>
[ConformanceSpec(
    "get-job-explanation.point-read",
    "GetJobExplanation returns explain sets for a known id and null otherwise",
    Area = "Reads",
    Contract = "GetJobExplanation returns the header, step, and checkpoint result sets for a matching job id and null when no row matches."
)]
public sealed class GetJobExplanationSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A known job id returns a populated header (Ready) with no steps or checkpoints")]
    public async Task Returns_explain_data_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
            ct
        );
        var jobId = results[0].JobId;

        var data = await Services.GetRequiredService<GetJobExplanationHandler>().ReadAsync(jobId, ct);

        Assert.NotNull(data);
        Assert.Equal(jobId, data!.Header.JobId);
        Assert.Equal(JobStatusCode.Ready, data.Header.Status);
        Assert.Equal(TestNamespace, data.Header.JobNamespace);
        Assert.Equal("add-numbers", data.Header.JobName);
        Assert.Null(data.Header.LeasedByWorkerId);
        Assert.Null(data.Header.LatestReasonCode);
        Assert.Empty(data.Steps);
        Assert.Empty(data.Checkpoints);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var data = await Services.GetRequiredService<GetJobExplanationHandler>().ReadAsync(long.MaxValue, ct);

        Assert.Null(data);
    }
}
