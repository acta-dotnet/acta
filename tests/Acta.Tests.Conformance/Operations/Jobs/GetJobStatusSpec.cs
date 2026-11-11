using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for <c>GetJobStatus</c>: a narrow status-only read that returns the
/// <c>JobStatusCode</c> for a known job id and <c>null</c> for an unknown id. Lighter than
/// <c>GetJob</c> - a single-column scalar projection.
/// </summary>
[ConformanceSpec(
    "get-job-status.status-read",
    "GetJobStatus returns the status for a known id and null for an unknown id",
    Area = "Reads",
    Contract = "GetJobStatus returns the current JobStatusCode for a matching job row and null when no row matches the supplied id."
)]
[Deploys("add-numbers")]
public sealed class GetJobStatusSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<GetJobStatusSpec>>
{
    [Fact(DisplayName = "A known job id returns its current JobStatusCode and a freshly enqueued job reads as Ready")]
    public async Task Returns_Ready_for_a_freshly_enqueued_job()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = Services.GetRequiredService<ISqlDialect>();
        var serializers = Services.GetRequiredService<IJobPayloadSerializerRegistry>();

        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(new AddNumbers(3, 4));
        var results = (
            await EnqueueTestOps.EnqueueJobsAsync(
                Services,
                [new JobEnqueueValues(NamespaceName: TestNamespace, JobName: "add-numbers", Input: payload)],
                ct
            )
        ).Jobs;
        var jobId = results[0].JobId;

        var status = await Services.GetRequiredService<GetJobStatusHandler>().ReadAsync(jobId, ct);

        Assert.Equal(JobStatusCode.Ready, status);
    }

    [Fact(DisplayName = "A status id this build does not know reads as Unspecified, as the full job read does")]
    public async Task An_unassigned_status_id_reads_as_unspecified()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(1, 2))), ct);

        // No member of JobStatusCode holds 230 and no check on jobs_state refuses it, so a newer Acta may write it.
        var rewritten = await Db.From<JobStateEntity>()
            .Where(r => r.Id == job.JobId)
            .UpdateOnlyAsync(() => new JobStateEntity { Status = (JobStatusCode)230 }, ct);
        Assert.Equal(1, rewritten);

        Assert.Equal(JobStatusCode.Unspecified, await Services.GetRequiredService<GetJobStatusHandler>().ReadAsync(job.JobId, ct));
        Assert.Equal(JobStatusCode.Unspecified, (await Jobs.GetAsync(JobLookup.ById(job.JobId), ct))!.Status);
    }

    [Fact(DisplayName = "An unknown job id returns null")]
    public async Task Returns_null_for_unknown_job_id()
    {
        var ct = TestContext.Current.CancellationToken;

        var status = await Services.GetRequiredService<GetJobStatusHandler>().ReadAsync(long.MaxValue, ct);

        Assert.Null(status);
    }
}
