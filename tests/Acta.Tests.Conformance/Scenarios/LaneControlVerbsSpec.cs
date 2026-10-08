using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Operator verbs keep a lane's order: a verb that would make a laned job runnable makes it Ready only
/// when it is the lane's lowest-id unfinished member and Blocked otherwise, and a paused member keeps
/// its place, so the lane waits for it rather than skipping ahead.
/// </summary>
[ConformanceSpec(
    "lanes.control-verbs",
    "Operator verbs keep a lane's order",
    Area = "Lanes",
    Contract = "Pause, resume, reschedule, restart, and reprioritize on a laned job never let it run ahead of an older unfinished member of its lane."
)]
public sealed class LaneControlVerbsSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    [Fact(DisplayName = "A paused follower holds the lane after the head finishes, and resuming it makes it the head")]
    public async Task Paused_follower_holds_the_lane_until_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", ct);
        var paused = await StepAsync("b", ct);
        var last = await StepAsync("c", ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(paused, ct: ct)).Action);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));

        Assert.Equal(JobStatusCode.Paused, await StatusAsync(paused, ct));
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(last, ct));

        var resumed = await Jobs.ResumeAsync(paused, ct: ct);
        Assert.Equal(JobStatusCode.Ready, resumed.Status);
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(last, ct));
    }

    [Fact(DisplayName = "Resume, reschedule, and restart leave a follower Blocked while an older member is unfinished")]
    public async Task Verbs_leave_a_follower_blocked_behind_the_head()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", ct);
        var follower = await StepAsync("b", ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(follower, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Blocked, (await Jobs.ResumeAsync(follower, ct: ct)).Status);

        Assert.Equal(JobStatusCode.Blocked, (await Jobs.RescheduleAsync(follower, DateTime.UtcNow.AddMinutes(-1), ct: ct)).Status);
        Assert.Equal(JobStatusCode.Blocked, (await Jobs.RestartAsync(follower, ct: ct)).Status);
        Assert.Equal(ControlAction.Applied, (await Jobs.ReprioritizeAsync(follower, JobPriorityCode.Critical, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Blocked, await StatusAsync(follower, ct));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));
        Assert.Equal(JobStatusCode.Ready, await StatusAsync(follower, ct));
    }

    [Fact(DisplayName = "A rescheduled follower keeps its later instant when it is promoted")]
    public async Task Rescheduled_follower_keeps_its_instant_on_promotion()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", ct);
        var follower = await StepAsync("b", ct);
        var later = DateTime.UtcNow.AddHours(1);

        Assert.Equal(JobStatusCode.Blocked, (await Jobs.RescheduleAsync(follower, later, ct: ct)).Status);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));

        var row = await ReadJobAsync(follower.JobId, ct);
        Assert.Equal(JobStatusCode.Ready, row.Status);
        Assert.True(row.NextRunAtUtc >= later.AddSeconds(-1), $"promotion moved the instant to {row.NextRunAtUtc:O}");
    }

    private async Task<JobEnqueueResponse> StepAsync(string label, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, "lane-step", JobPayload.Json(new LaneStep("orders", label)), Lane: "orders"),
            ct
        );

    private async Task<JobStatusCode> StatusAsync(JobEnqueueResponse outcome, CancellationToken ct) =>
        (await ReadJobAsync(outcome.JobId, ct)).Status;
}
