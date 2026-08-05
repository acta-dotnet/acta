using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Jobs;

/// <summary>
/// Operator ReprioritizeAsync: changes a job's claim priority. Unlike the other lifecycle verbs, any
/// non-terminal row (including in-flight) accepts the change - only the NEXT claim is affected, so
/// status and cursor are left alone; only terminal jobs are rejected.
/// </summary>
[ConformanceSpec(
    "job.reprioritize",
    "Operator reprioritize changes claim priority, rejecting only terminal jobs.",
    Area = "Control",
    Contract = "ReprioritizeAsync sets priority_code on any non-terminal row (including in-flight), leaving status and cursor unchanged, and rejects terminal rows.",
    Arrange = "A Ready job, one job mid-execution, one completed job, and no job for an unknown lookup.",
    Act = "ReprioritizeAsync is invoked against each job with a new priority.",
    Assert = "The Ready and executing jobs adopt the new priority with an audited event, the terminal job is rejected unchanged, and the unknown lookup is NotFound."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.ReprioritizeJobAsync))]
public abstract class JobReprioritizeSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "ReprioritizeAsync changes a Ready job's priority and bumps the runtime version")]
    public async Task Reprioritize_ready_job_changes_priority()
    {
        var ct = TestContext.Current.CancellationToken;

        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        var before = await ReadJobAsync(enqueued.JobId, ct);

        var result = await Jobs.ReprioritizeAsync(enqueued, JobPriorityCode.Critical, "ops", "spec-actor", ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);

        var snapshot = await Jobs.GetAsync(enqueued, ct);
        Assert.NotNull(snapshot);
        Assert.Equal(JobStatusCode.Ready, snapshot!.Status);
        Assert.Equal(JobPriorityCode.Critical, snapshot.Priority);

        var after = await ReadJobAsync(enqueued.JobId, ct);
        Assert.True(after.Version > before.Version);

        var evt = await ReadSingleEventAsync(enqueued.JobId, JobEventCode.JobReprioritized, ct);
        Assert.Equal("spec-actor", evt.ActorKey);
    }

    [Fact(DisplayName = "ReprioritizeAsync accepts an in-flight job without changing its status")]
    public async Task Reprioritize_executing_job_changes_priority_in_place()
    {
        var ct = TestContext.Current.CancellationToken;

        var executingJob = await EnqueueAsync(ct);
        await SetJobStatusAsync(Db, executingJob, (byte)JobStatusCode.Executing, ct);

        var result = await Jobs.ReprioritizeAsync(JobLookup.ById(executingJob), JobPriorityCode.High, ct: ct);

        Assert.Equal(JobControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Executing, result.Status);

        var after = await ReadJobAsync(executingJob, ct);
        Assert.Equal(JobStatusCode.Executing, after.Status);
        Assert.Equal(JobPriorityCode.High, after.Priority);
    }

    [Fact(DisplayName = "ReprioritizeAsync rejects a terminal job without mutating it")]
    public async Task Reprioritize_rejects_terminal()
    {
        var ct = TestContext.Current.CancellationToken;

        var completedJob = (await EnqueueAndRunAsync("add-numbers", new AddNumbers(2, 3), ct)).JobId;
        var before = await ReadJobAsync(completedJob, ct);

        var result = await Jobs.ReprioritizeAsync(JobLookup.ById(completedJob), JobPriorityCode.Bulk, ct: ct);

        Assert.Equal(JobControlAction.Rejected, result.Action);
        Assert.Equal(JobStatusCode.Succeeded, result.Status);

        var after = await ReadJobAsync(completedJob, ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Priority, after.Priority);
    }

    [Fact(DisplayName = "ReprioritizeAsync returns NotFound for an unknown lookup")]
    public async Task Reprioritize_unknown_is_notfound()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await Jobs.ReprioritizeAsync(JobLookup.ById(999_999_999_999L), JobPriorityCode.High, ct: ct);
        Assert.Equal(JobControlAction.NotFound, result.Action);
    }

    // ---------- helpers ----------

    private async Task<long> EnqueueAsync(CancellationToken ct)
    {
        var enqueued = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "add-numbers", JobPayload.Json(new AddNumbers(2, 3))),
            ct
        );
        return enqueued.JobId;
    }

    private static Task SetJobStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );
}
