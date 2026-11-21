using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJob</c>: a point-read that returns the <c>GetJobResponse</c> projection for a
/// known job id, and <c>null</c> for an unknown id. Exercises the column mapping (id, status,
/// idempotency key, namespace, name, definition, priority, execution number, parent, next run,
/// the input's and the result's format and size, tenant) and the absent-row sentinel.
/// </summary>
[ConformanceSpec(
    "get-job.point-read",
    "GetJob returns the snapshot for a known id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJob returns the GetJobResponse projection for a matching job row and null when no row matches the supplied id."
)]
[Deploys("add-numbers", "cancellable")]
public sealed class GetJobSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobSpec>>
{
    [Fact(DisplayName = "A known job id returns a populated GetJobResponse whose id and Ready status match the enqueued row")]
    public async Task Returns_snapshot_for_known_job_id()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var idempotencyKey = Unique("get-job");
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueJobsAsync(
                Services,
                [
                    new JobEnqueueValues(
                        NamespaceName: TestNamespace,
                        JobName: "add-numbers",
                        Input: payload,
                        IdempotencyKey: idempotencyKey
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
        Assert.Equal(idempotencyKey, snapshot.IdempotencyKey);
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
        var tenantName = Unique("get-job-ten");
        var tenantId = (await Operations.Tenants.RegisterAsync(tenantName, null, null, ct)).TenantId;

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(1, 2));
        var results = (
            await EnqueueTestOps.EnqueueJobsAsync(
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

    [Fact(DisplayName = "The snapshot states the input's and the result's format and size, and no size where there is none")]
    public async Task Snapshot_states_the_input_and_the_result_by_format_and_size()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = JobPayload.Json(new AddNumbers(2, 3));
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", input), ct);

        var ready = (await Jobs.GetAsync(job, ct))!;
        Assert.Equal(input.Format.Id, ready.InputFormatId);
        Assert.Equal((long)input.Data.Length, ready.InputByteLength);
        Assert.Equal(0, ready.ResultFormatId);
        Assert.Null(ready.ResultByteLength);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(job, ct));
        var result = (await Jobs.GetResultAsync(job, ct))!.Result;
        var done = (await Jobs.GetAsync(job, ct))!;
        Assert.Equal(result.Format.Id, done.ResultFormatId);
        Assert.Equal((long)result.Data.Length, done.ResultByteLength);

        var noInput = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "cancellable", JobPayload.None), ct);
        var none = (await Jobs.GetAsync(noInput, ct))!;
        Assert.Equal(0, none.InputFormatId);
        Assert.Null(none.InputByteLength);

        // A restart clears the result, and the snapshot says so: format 0 and no size.
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(job, "run it again", ct: ct)).Action);
        var restarted = (await Jobs.GetAsync(job, ct))!;
        Assert.Equal(0, restarted.ResultFormatId);
        Assert.Null(restarted.ResultByteLength);
    }

    [Fact(DisplayName = "A size counts stored bytes: a format with no bytes is 0, and multi-byte UTF-8 counts every byte")]
    public async Task Snapshot_sizes_count_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var empty = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Bytes([])), ct);
        await StoreResultAsync(empty.JobId, JobPayloadFormat.Bytes.Id, [], ct);

        var stated = (await Jobs.GetAsync(empty, ct))!;
        Assert.Equal(JobPayloadFormat.Bytes.Id, stated.InputFormatId);
        Assert.Equal(0L, stated.InputByteLength);
        Assert.Equal(JobPayloadFormat.Bytes.Id, stated.ResultFormatId);
        Assert.Equal(0L, stated.ResultByteLength);

        const string text = "{\"message\":\"Zoë ✓ 日本語 😀\"}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        Assert.NotEqual(text.Length, bytes.Length);
        var wide = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.FromBytes(JobPayloadFormat.Json, bytes)),
            ct
        );
        await StoreResultAsync(wide.JobId, JobPayloadFormat.Json.Id, bytes, ct);

        var multiByte = (await Jobs.GetAsync(wide, ct))!;
        Assert.Equal((long)bytes.Length, multiByte.InputByteLength);
        Assert.Equal((long)bytes.Length, multiByte.ResultByteLength);
    }

    // A result as a completion leaves one on a job that is not executing, stored with its exact bytes.
    private Task StoreResultAsync(long jobId, byte formatId, byte[] result, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET result_format_id = @p_format, result = @p_value WHERE job_id = @p_job_id",
            ct,
            ("@p_format", formatId),
            ("@p_value", result),
            ("@p_job_id", jobId)
        );

    [Fact(DisplayName = "An executing job past its lease reads LeaseLapsed, and one inside its lease does not")]
    public async Task An_executing_job_past_its_lease_reads_lapsed()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new AddNumbers(1, 2), ct: ct);
        await JobStateStaging.SetStatusAsync(Db, job.JobId, JobStatusCode.Executing, ct);
        Assert.False((await Jobs.GetAsync(job, ct))!.LeaseLapsed);

        var past = DateTime.UtcNow.AddMinutes(-5);
        await Db.From<JobStateEntity>()
            .Where(r => r.Id == job.JobId)
            .UpdateOnlyAsync(() => new JobStateEntity { LeaseExpiresAtUtc = past }, ct);
        Assert.True((await Jobs.GetAsync(job, ct))!.LeaseLapsed);
    }
}
