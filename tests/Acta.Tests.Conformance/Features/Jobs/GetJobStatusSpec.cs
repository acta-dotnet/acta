using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>GetJobStatus</c>: a narrow status-only read that returns the
/// <c>JobStatusCode</c> for a known job id and <c>null</c> for an unknown id. Lighter than
/// <c>GetJob</c> - a single-column scalar projection.
/// </summary>
[ConformanceSpec(
    "get-job-status.status-read",
    "GetJobStatus returns the status for a known id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJobStatus returns the current JobStatusCode for a matching job row and null when no row matches the supplied id.",
    Arrange = "A job is freshly enqueued so a known job id exists in Ready.",
    Act = "GetJobStatus is called with the enqueued id and then with an id that matches no row.",
    Assert = "The known id returns JobStatusCode Ready and the unknown id returns null."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobStatusAsync))]
public abstract class GetJobStatusSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A known job id returns its current JobStatusCode and a freshly enqueued job reads as Ready")]
    public async Task Returns_Ready_for_a_freshly_enqueued_job()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(3, 4));
        var results = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
            ct
        );
        var jobId = results[0].JobId;

        var status = await Services.GetRequiredService<IJobStore>().GetJobStatusAsync(jobId, ct);

        Assert.Equal(JobStatusCode.Ready, status);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var status = await Services.GetRequiredService<IJobStore>().GetJobStatusAsync(long.MaxValue, ct);

        Assert.Null(status);
    }
}
