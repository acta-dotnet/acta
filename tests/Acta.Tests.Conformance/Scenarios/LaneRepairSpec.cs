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
    Contract = "A recovery pass promotes the lowest Blocked member of a lane with no live head, records why, and wakes the namespace, leaving lanes with a live head alone."
)]
public sealed class LaneRepairSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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
        var repaired = await ReadSingleEventAsync(follower.JobId, EventCode.JobResumed, ct);
        Assert.Equal(EventReason.LaneRepaired, EventDetail(repaired, "reason"));
        Assert.Null(EventDetail(repaired, "actor"));
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

    [Fact(DisplayName = "A pass in another namespace leaves this namespace's stranded lane alone")]
    public async Task Pass_in_another_namespace_leaves_the_lane_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("orders", "a", ct);
        var follower = await StepAsync("orders", "b", ct);
        await SetStatusAsync(head, JobStatusCode.Succeeded, ct);
        var otherName = Unique("other");
        var otherId = await new ActaTestSeeder(Db).SeedJobNamespaceAsync(otherName, "test", ct);

        await Services.GetRequiredService<RecoveryPass>().RunAsync(otherId, otherName, ct);

        await AssertUntouchedAsync(follower, ct);
    }

    [Fact(DisplayName = "One pass repairs at most a hundred stranded lanes of the thousand it visits")]
    public async Task One_pass_is_capped()
    {
        var ct = TestContext.Current.CancellationToken;
        // More stranded lanes than the pass visits, so its whole window is stranded and only the cap binds.
        const int Lanes = 1100;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var enqueued = new List<EnqueueJobResponse>();
        // Each chunk holds whole lanes, head before follower.
        foreach (
            var chunk in Enumerable
                .Range(0, Lanes)
                .SelectMany(i => new[] { Step($"cap-{i}", "head"), Step($"cap-{i}", "follower") })
                .Chunk(1000)
        )
        {
            enqueued.AddRange((await Jobs.EnqueueBatchAsync(chunk, ct)).Jobs);
        }
        // Every head hand-set to Succeeded, so every lane is stranded behind a Blocked follower.
        await JobStateStaging.UpdateAsync(
            Db,
            enqueued.Where((_, i) => i % 2 == 0).Select(head => head.JobId),
            $"job_status_code = {(int)JobStatusCode.Succeeded}",
            ct
        );

        await Services.GetRequiredService<RecoveryPass>().RunAsync(ns, TestNamespace, ct);

        Assert.Equal(
            100,
            await Db.From<JobStateEntity>()
                .Where(r => r.NamespaceId == ns && r.LaneId != null && r.Status == JobStatusCode.Ready)
                .CountAsync(ct)
        );
    }

    private Task PassAsync(CancellationToken ct) =>
        Services.GetRequiredService<RecoveryPass>().RunAsync(Runtime.RegisteredNamespaceIds[TestNamespace], TestNamespace, ct);

    private EnqueueJobRequest Step(string lane, string label) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane);

    private async Task AssertUntouchedAsync(EnqueueJobResponse follower, CancellationToken ct)
    {
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(follower, ct));
        Assert.Equal(0, await CountReasonAsync(follower.JobId, EventReason.LaneRepaired, ct));
    }

    // A hand UPDATE, the way an operator or a bug strands a lane: no settle runs, so nothing promotes.
    private Task SetStatusAsync(EnqueueJobResponse job, JobStatusCode status, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            $"UPDATE {{schema}}.jobs_state SET job_status_code = {(int)status} WHERE job_id = @p_id",
            ct,
            ("@p_id", job.JobId)
        );

    private async Task<EnqueueJobResponse> StepAsync(string lane, string label, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane, label)), Lane: lane),
            ct
        );

    private async Task<JobStatusCode> StatusAsync(EnqueueJobResponse outcome, CancellationToken ct) =>
        (await ReadJobAsync(outcome.JobId, ct)).Status;
}
