using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// The recovery pass repairs a stranded lane, one whose lowest-id unfinished member is Blocked, so
/// nothing ahead of it will ever settle and promote it. A lane whose head is live, even when that head
/// is held Paused or Suspended, is left alone.
/// </summary>
[ConformanceSpec(
    "lanes.stranded-repair",
    "The recovery pass releases the Blocked head of a stranded lane",
    Area = "Lanes",
    Contract = "A recovery pass promotes the lowest Blocked member of a lane with no live head, records why, and wakes the namespace, leaving lanes with a live head alone.",
    Arrange = "Laned heads with Blocked followers sit in a private namespace, one head hand-set to Succeeded and the others Ready, Paused, or Suspended.",
    Act = "One recovery pass runs for the namespace.",
    Assert = "Only the stranded lane's follower is Ready, with a sys event carrying job.lane-repaired and a namespace wake."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ReclaimStuckJobsAsync))]
public abstract class LaneRepairSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private readonly RecordingWakeup _wakeups = new();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Registered ahead of UseActa's TryAddSingleton, so the pass publishes through the recorder.
        services.AddSingleton<IWorkerWakeup>(_wakeups);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A lane whose head was hand-set to Succeeded is repaired by one pass, with its event and a wake")]
    public async Task Stranded_lane_is_repaired_by_one_pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("orders", "a", ct);
        var follower = await StepAsync("orders", "b", ct);
        var last = await StepAsync("orders", "c", ct);
        await SetStatusAsync(head, JobStatusCode.Succeeded, ct);
        var before = _wakeups.Published.Count;

        await PassAsync(ct);

        Assert.Equal(JobStatusCode.Ready, await StatusAsync(follower, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(last, ct));
        var repaired = Assert.Single(
            await Db.From<JobEvent>()
                .Where(e => e.JobId == follower.JobId && e.ReasonCode == JobEventReasonCode.JobLaneRepaired)
                .ToListAsync(ct)
        );
        Assert.Equal(ActorCode.Sys, repaired.ActorCode);
        Assert.Equal(JobStatusCode.Blocked, repaired.FromStatus);
        Assert.Equal(JobStatusCode.Ready, repaired.ToStatus);
        Assert.Contains(_wakeups.Published.Skip(before), c => c.Kind == WorkerWakeupChannelKind.WorkerNamespace);
    }

    [Fact(DisplayName = "A lane with a Ready head is left untouched by the pass")]
    public async Task Healthy_lane_is_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await StepAsync("orders", "a", ct);
        var follower = await StepAsync("orders", "b", ct);

        await PassAsync(ct);

        await AssertUntouchedAsync(follower, ct);
    }

    [Fact(DisplayName = "A lane whose head is Paused or Suspended is held, not stranded, and the pass leaves it")]
    public async Task Held_head_is_not_repaired()
    {
        var ct = TestContext.Current.CancellationToken;
        var paused = await StepAsync("paused-lane", "a", ct);
        var behindPaused = await StepAsync("paused-lane", "b", ct);
        var suspended = await StepAsync("suspended-lane", "a", ct);
        var behindSuspended = await StepAsync("suspended-lane", "b", ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(paused, ct: ct)).Action);
        await SetStatusAsync(suspended, JobStatusCode.Suspended, ct);

        await PassAsync(ct);

        await AssertUntouchedAsync(behindPaused, ct);
        await AssertUntouchedAsync(behindSuspended, ct);
    }

    private Task PassAsync(CancellationToken ct) =>
        Services.GetRequiredService<RecoveryPass>().RunAsync(Runtime.RegisteredNamespaceIds[TestNamespace], TestNamespace, ct);

    private async Task AssertUntouchedAsync(JobEnqueueOutcome follower, CancellationToken ct)
    {
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(follower, ct));
        Assert.Empty(
            await Db.From<JobEvent>()
                .Where(e => e.JobId == follower.JobId && e.ReasonCode == JobEventReasonCode.JobLaneRepaired)
                .ToListAsync(ct)
        );
    }

    // A hand UPDATE, the way an operator or a bug strands a lane: no settle runs, so nothing promotes.
    private Task SetStatusAsync(JobEnqueueOutcome job, JobStatusCode status, CancellationToken ct) =>
        Db.ExecuteRawAsync($"UPDATE {{schema}}.runtimes SET status_code = {(int)status} WHERE job_id = @p_id", ct, ("@p_id", job.JobId));

    private async Task<JobEnqueueOutcome> StepAsync(string lane, string label, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane),
            ct
        );

    private async Task<JobStatusCode> StatusAsync(JobEnqueueOutcome outcome, CancellationToken ct) =>
        (await ReadJobAsync(outcome.JobId, ct)).Status;
}
