using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJob</c>: a point-read that returns the <c>GetJobResponse</c> projection for a
/// known job id, and <c>null</c> for an unknown id. Exercises the column mapping (id, namespace,
/// name, status, failure_count, reason_code) and the absent-row sentinel.
/// </summary>
[ConformanceSpec(
    "get-job.point-read",
    "GetJob returns the snapshot for a known id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJob returns the GetJobResponse projection for a matching job row and null when no row matches the supplied id."
)]
public sealed class GetJobSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A known job id returns a populated GetJobResponse whose id and Ready status match the enqueued row")]
    public async Task Returns_snapshot_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var deduplicationKey = TestKey("get-job");
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueBatchAsync(
                Services,
                [
                    new JobEnqueueValues(
                        NamespaceName: TestNamespace,
                        JobName: "add-numbers",
                        Input: payload,
                        DeduplicationKey: deduplicationKey
                    ),
                ],
                ct
            )
        ).Jobs;
        var jobId = results[0].JobId;

        var snapshot = await Services.GetRequiredService<GetJobHandler>().ReadAsync(jobId, ct);

        Assert.NotNull(snapshot);
        Assert.Equal(jobId, snapshot!.JobId);
        Assert.Equal(JobStatusCode.Ready, snapshot.Status);
        Assert.Equal(deduplicationKey, snapshot.DeduplicationKey);
        Assert.Equal(TestNamespace, snapshot.JobNamespace);
        Assert.Equal("add-numbers", snapshot.JobName);
        // The job's namespace+name pair addresses the catalog row its definition id points at.
        var definition = await Services.GetRequiredService<IDefinitions>().GetAsync(snapshot.JobNamespace, snapshot.JobName, ct);
        Assert.NotNull(definition);
        Assert.Equal("add-numbers", definition!.JobName);
        Assert.Equal(TestNamespace, definition.JobNamespace);
        Assert.Equal(snapshot.DefinitionId, definition.DefinitionId);
        Assert.Equal(JobPriorityCode.Normal, snapshot.Priority);
        Assert.Equal(0, snapshot.ExecutionNumber);
        Assert.Null(snapshot.ParentJobId);
        Assert.NotNull(snapshot.NextRunAtUtc);
        Assert.NotEqual(0, snapshot.InputFormatId);
        Assert.Null(snapshot.TenantId);
        Assert.Null(snapshot.TenantName);
    }

    [Fact(DisplayName = "A tenant-scoped job's snapshot carries the tenant id and its external key")]
    public async Task Snapshot_carries_tenant_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();
        var tenantName = TestKey("get-job-ten");
        var tenantId = (await Operations.Tenants.RegisterAsync(tenantName, null, null, ct)).TenantId;

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueBatchAsync(
                Services,
                [new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload, TenantName: tenantName)],
                ct
            )
        ).Jobs;

        var snapshot = await Services.GetRequiredService<GetJobHandler>().ReadAsync(results[0].JobId, ct);

        Assert.NotNull(snapshot);
        Assert.Equal(tenantId, snapshot!.TenantId);
        Assert.Equal(tenantName, snapshot.TenantName);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var snapshot = await Services.GetRequiredService<GetJobHandler>().ReadAsync(long.MaxValue, ct);

        Assert.Null(snapshot);
    }
}
