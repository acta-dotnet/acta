using Acta.Features.Jobs;
using Acta.Payloads;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Conformance for <c>GetJob</c>: a point-read that returns the <c>JobSnapshot</c> projection for a
/// known job id, and <c>null</c> for an unknown id. Exercises the column mapping (id, namespace,
/// name, status, failure_count, reason_code) and the absent-row sentinel.
/// </summary>
[ConformanceSpec(
    "get-job.point-read",
    "GetJob returns the snapshot for a known id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJob returns the JobSnapshot projection for a matching job row and null when no row matches the supplied id.",
    Arrange = "A job is enqueued so a known job id exists.",
    Act = "GetJob is called with the enqueued id and then with an id that matches no row.",
    Assert = "The known id returns a populated JobSnapshot with Ready status and the unknown id returns null."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.GetJobAsync))]
public abstract class GetJobSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A known job id returns a populated JobSnapshot whose id and Ready status match the enqueued row")]
    public async Task Returns_snapshot_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var dialect = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var deduplicationKey = TestKey("get-job");
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = await EnqueueTestOps.EnqueueBatchAsync(
            Services,
            [new JobEnqueueRow(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload, DeduplicationKey: deduplicationKey)],
            ct
        );
        var jobId = results[0].JobId;

        var snapshot = await Services.GetRequiredService<IJobStore>().GetJobAsync(jobId, ct);

        Assert.NotNull(snapshot);
        Assert.Equal(jobId, snapshot!.JobId);
        Assert.Equal(JobStatusCode.Ready, snapshot.Status);
        Assert.Equal(deduplicationKey, snapshot.DeduplicationKey);
        Assert.Equal(TestNamespace, snapshot.JobNamespace);
        Assert.Equal("add-numbers", snapshot.JobName);
        Assert.Equal(JobPriorityCode.Normal, snapshot.Priority);
        Assert.Equal(0, snapshot.ExecutionNumber);
        Assert.Null(snapshot.ParentJobId);
        Assert.NotNull(snapshot.NextRunAtUtc);
        Assert.NotEqual(0, snapshot.InputFormatId);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var snapshot = await Services.GetRequiredService<IJobStore>().GetJobAsync(long.MaxValue, ct);

        Assert.Null(snapshot);
    }
}
