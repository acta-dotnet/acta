using System.Text;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Jobs;

/// <summary>
/// Conformance for the operator guide's table "What each verb does from each status": a verb applies or is rejected
/// by the job's status alone, and an applied verb leaves the job in the status the table names. The refusals that depend
/// on more than the status (a job with children, a lane's other members, an outstanding firing, a live schedule) have
/// specs of their own, as do a purge and an input amendment of an Idle permanent job. A status is staged on the row,
/// so the matrix holds the verbs' status rules, not what a real wait or attempt adds to them.
/// </summary>
[ConformanceSpec(
    "control.status-matrix",
    "Each control verb applies or is rejected by the job's status",
    Area = "Control",
    Contract = "A control verb applies from each status the operator guide's table marks applied and is rejected from every other, for an ordinary and an Idle permanent job."
)]
[Deploys("lane-step", "recurring-ping")]
public sealed class JobControlStatusMatrixSpec : ActaRuntimeTestBase<ConformanceFixture, JobsOf<JobControlStatusMatrixSpec>>
{
    // The table's columns for an ordinary job, in its order: R B S P X, then the three finished statuses.
    private static readonly JobStatusCode[] Statuses =
    [
        JobStatusCode.Ready,
        JobStatusCode.Blocked,
        JobStatusCode.Suspended,
        JobStatusCode.Paused,
        JobStatusCode.Executing,
        JobStatusCode.Succeeded,
        JobStatusCode.Failed,
        JobStatusCode.Cancelled,
    ];

    [Theory(DisplayName = "A verb applies (A) or is rejected (-) from each status of an ordinary job as the table says")]
    [InlineData("pause", "AAAA----")]
    [InlineData("resume", "---A----")]
    [InlineData("restart", "AAAA-AAA")]
    [InlineData("cancel", "AAAAA---")]
    [InlineData("purge", "-----AAA")]
    [InlineData("reschedule", "AAAA----")]
    [InlineData("reprioritize", "AAAAA---")]
    [InlineData("input", "AAAA-AAA")]
    [InlineData("signal", "AAAAA---")]
    public async Task Verb_by_status(string verb, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var actual = new StringBuilder();
        foreach (var status in Statuses)
        {
            var job = await StageAsync(status, ct);
            var action = await ApplyAsync(verb, job, ct);
            actual.Append(Mark(action));
            if (action == ControlAction.Applied)
            {
                Assert.Equal(After(verb, status), (await Jobs.GetStatusAsync(job, ct))?.Status);
            }
        }

        Assert.Equal(expected, actual.ToString());
    }

    [Theory(DisplayName = "A verb applies or is rejected from an Idle permanent job as the table says")]
    [InlineData("pause", 'A')]
    [InlineData("resume", '-')]
    [InlineData("restart", 'A')]
    [InlineData("cancel", 'A')]
    [InlineData("reschedule", 'A')]
    [InlineData("reprioritize", 'A')]
    [InlineData("signal", 'A')]
    public async Task Verb_on_an_idle_permanent_job(string verb, char expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, "recurring-ping", ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        Assert.Equal(expected, Mark(await ApplyAsync(verb, JobLookup.ById(slotId), ct)));
    }

    /// <summary>
    /// The table's "Result on applied": where the verb leaves the job, null when the row is gone. A lane follower stays
    /// behind its head, and a staged Suspended job waits on nothing a signal could release.
    /// </summary>
    private static JobStatusCode? After(string verb, JobStatusCode from) =>
        verb switch
        {
            "pause" => JobStatusCode.Paused,
            "resume" => JobStatusCode.Ready,
            "cancel" => JobStatusCode.Cancelled,
            "purge" => null,
            "restart" or "reschedule" => from == JobStatusCode.Blocked ? JobStatusCode.Blocked : JobStatusCode.Ready,
            _ => from,
        };

    private static char Mark(ControlAction action) =>
        action switch
        {
            ControlAction.Applied => 'A',
            ControlAction.Rejected => '-',
            _ => '?',
        };

    private async Task<ControlAction> ApplyAsync(string verb, JobLookup job, CancellationToken ct) =>
        verb switch
        {
            "pause" => (await Jobs.PauseAsync(job, ct: ct)).Action,
            "resume" => (await Jobs.ResumeAsync(job, ct: ct)).Action,
            "restart" => (await Jobs.RestartAsync(job, ct: ct)).Action,
            "cancel" => (await Jobs.CancelAsync(job, ct: ct)).Action,
            "purge" => (await Jobs.PurgeAsync(job, ct: ct)).Action,
            "reschedule" => (await Jobs.RescheduleAsync(job, DateTime.UtcNow.AddHours(1), ct: ct)).Action,
            "reprioritize" => (await Jobs.ReprioritizeAsync(job, JobPriorityCode.High, ct: ct)).Action,
            "input" => (await Jobs.UpdateInputAsync(job, JobPayload.Json(new LaneStep("", "amended")), ct: ct)).Action,
            _ => (await Jobs.RaiseSignalAsync(job, "go", JobPayload.None, ct: ct)).Action,
        };

    /// <summary>A job standing in <paramref name="status"/>: Blocked behind its lane's head, the others by their stored status.</summary>
    private async Task<JobLookup> StageAsync(JobStatusCode status, CancellationToken ct)
    {
        if (status == JobStatusCode.Blocked)
        {
            var lane = Unique("matrix");
            await Jobs.EnqueueAsync(Step(lane), ct);
            var follower = await Jobs.EnqueueAsync(Step(lane), ct);
            Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(follower.JobId, ct)).Status);
            return follower;
        }

        var job = await Jobs.EnqueueAsync(Step(lane: null), ct);
        if (status != JobStatusCode.Ready)
        {
            await JobStateStaging.SetStatusAsync(Db, job.JobId, status, ct);
        }
        return job;
    }

    private EnqueueJobRequest Step(string? lane) =>
        new(TestNamespace, "lane-step", JobPayload.Json(new LaneStep(lane ?? "", "matrix")), Lane: lane);
}
