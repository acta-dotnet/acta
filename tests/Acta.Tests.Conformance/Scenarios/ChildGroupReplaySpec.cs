using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Conformance for a map's replay: it finds the children it started by name and starts only the rest, so it builds none
/// of their inputs again and still names the child it waits on.
/// </summary>
[ConformanceSpec(
    "child-jobs.group-replay",
    "A replayed map starts only the children it has not started",
    Area = "ChildJobs",
    Contract = "A replayed map answers the children it started from their rows, builds none of their inputs again, and names the child it waits on."
)]
[Deploys("job-child-echo", "job-parent-map-counted")]
public sealed class ChildGroupReplaySpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<ChildGroupReplaySpec>>
{
    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    [Fact(DisplayName = "A replayed map answers the children it started from their rows and builds none of their inputs again")]
    public async Task A_replayed_map_starts_no_child_it_already_started()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-map-counted", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        await SetJobStatusReadyAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(3, ChildJobProbes.MapInputsBuilt[parent.JobId]);
        Assert.Equal(3, (await Execution.ListChildJobIdsAsync(parent.JobId, ct)).ChildJobIds.Count);
    }

    [Fact(DisplayName = "A map replayed without starting a child names the child it waits on")]
    public async Task A_replay_that_started_nothing_names_its_child()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = await Jobs.EnqueueAsync(new EnqueueJobRequest(TestNamespace, "job-parent-map-counted", JobPayload.None), ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        await SetJobStatusReadyAsync(parent.JobId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(parent, ct));

        Assert.Equal(3, ChildJobProbes.MapInputsBuilt[parent.JobId]);
        Assert.Equal(2, await CountEventsAsync(parent.JobId, EventCode.JobSuspended, ct));
        Assert.Equal("Child 'echo/1' pending.", (await ReadLatestEventAsync(parent.JobId, EventCode.JobSuspended, ct)).EventMessage);
    }

    // Forces a replay of a parent parked on a child that has not landed.
    private Task SetJobStatusReadyAsync(long jobId, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = @p_now WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Ready),
            ("@p_now", DateTime.UtcNow.AddMinutes(-1)),
            ("@p_id", jobId)
        );
}
