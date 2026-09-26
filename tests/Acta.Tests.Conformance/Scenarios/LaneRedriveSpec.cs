using System.Text;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Restarting a finished laned job cannot reopen it in place, which would put it ahead of members that
/// already ran after it; it redrives the job instead, as a new job entering the lane at its tail.
/// </summary>
[ConformanceSpec(
    "lanes.redrive",
    "Restarting a finished laned job redrives it at the lane's tail",
    Area = "Lanes",
    Contract = "A restart of a finished laned job enqueues a copy at its lane's tail, keeps the finished row, links both by events, and returns the new job.",
    Arrange = "A laned head has run to Succeeded while a follower behind it is still unfinished.",
    Act = "An operator restarts the finished head.",
    Assert = "A new job with the same definition, input, lane, and priority waits behind the follower, the old row stays Succeeded, and each row's event names the other."
)]
[CoversStoreMethod(typeof(IJobStore), nameof(IJobStore.RecordJobRedriveAsync))]
public abstract class LaneRedriveSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    [Fact(DisplayName = "A restarted finished head is redriven behind the lane's unfinished follower")]
    public async Task Restarted_head_is_redriven_at_the_tail()
    {
        var ct = TestContext.Current.CancellationToken;
        var head = await StepAsync("a", JobPriorityCode.High, ct);
        var follower = await StepAsync("b", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, head.JobId, ct));

        var restarted = await Jobs.RestartAsync(head, "run it again", ct: ct);

        Assert.Equal(ControlAction.Applied, restarted.Action);
        Assert.Equal(JobStatusCode.Succeeded, restarted.Status);
        var redriveRef = Assert.NotNull(restarted.RedriveJobRef);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(head.JobId, ct)).Status);

        var redriven = (await Jobs.GetAsync(JobLookup.ByRef(redriveRef), ct))!;
        Assert.Equal(restarted.RedriveJobId, redriven.JobId);
        Assert.Equal("lane-step", redriven.JobName);
        Assert.Equal("orders", redriven.Lane);
        Assert.Equal(JobPriorityCode.High, redriven.Priority);
        Assert.Equal(JobStatusCode.Blocked, redriven.Status);
        Assert.Equal(follower.JobRef, redriven.BlockedBehindJobRef);
        var input = Assert.NotNull(await Jobs.GetInputAsync(JobLookup.ByRef(redriveRef), ct));
        var original = Assert.NotNull(await Jobs.GetInputAsync(head, ct));
        Assert.Equal(original.Format, input.Format);
        Assert.Equal(original.Data.ToArray(), input.Data.ToArray());

        Assert.Contains(redriveRef.ToString(), await RedriveDetailAsync(head.JobId, ct));
        Assert.Contains(head.JobRef.ToString(), await RedriveDetailAsync(redriven.JobId, ct));
    }

    [Fact(DisplayName = "A restarted finished job in an idle lane is redriven Ready")]
    public async Task Restarted_job_in_an_idle_lane_is_redriven_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        var only = await StepAsync("a", null, ct);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, only.JobId, ct));

        var restarted = await Jobs.RestartAsync(only, ct: ct);

        var redriven = await Jobs.GetAsync(JobLookup.ByRef(Assert.NotNull(restarted.RedriveJobRef)), ct);
        Assert.Equal(JobStatusCode.Ready, redriven!.Status);
        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, redriven.JobId, ct));
    }

    private async Task<string> RedriveDetailAsync(long jobId, CancellationToken ct)
    {
        var events = await Db.From<JobEvent>().Where(e => e.JobId == jobId && e.EventCode == EventCode.JobRedriven).ToListAsync(ct);
        return Encoding.UTF8.GetString(Assert.Single(events).Detail ?? []);
    }

    private async Task<JobEnqueueOutcome> StepAsync(string label, JobPriorityCode? priority, CancellationToken ct) =>
        await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                "lane-step",
                JobPayload.Json(new LaneStep("orders", label)),
                Priority: priority,
                Lane: "orders"
            ),
            ct
        );
}
