using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Engine.Worker;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// An operator's pause or cancel of a recurring job holds until an operator lifts it: startup, the schedule verbs,
/// and the admitter leave a held or finished slot alone. A slot whose schedules offer no run, a deploy that dropped
/// them included, stays Idle and fires again once a schedule offers one.
/// </summary>
[ConformanceSpec(
    "schedule.job-pause-holds",
    "A paused or cancelled recurring job stays so until an operator lifts it",
    Area = "Scheduling",
    Contract = "A recurring job an operator paused or cancelled keeps that status through worker restarts, schedule edits, and trigger-now."
)]
public sealed class RecurringJobPauseSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private ScheduleLookup Schedule => new(TestNamespace, JobName, ScheduleName);

    private async Task<JobLookup> SlotAsync(CancellationToken ct) =>
        JobLookup.ById(await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct));

    [Fact(DisplayName = "A paused recurring job stays paused when the worker starts again, and a resume returns it to Idle")]
    public async Task A_paused_recurring_job_stays_paused_across_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(await SlotAsync(ct), ct: ct)).Action);

        // A second startup on the same runtime re-runs registration and the schedule reconcile.
        await Runtime.InitializeAsync(ct);

        Assert.Equal(JobStatusCode.Paused, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(await SlotAsync(ct), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Idle, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
    }

    [Fact(DisplayName = "Pausing and resuming the only schedule of a paused recurring job leaves the job paused")]
    public async Task Pausing_and_resuming_its_only_schedule_leaves_a_paused_job_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(await SlotAsync(ct), ct: ct)).Action);
        var lookup = Schedule;

        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.PauseAsync(lookup, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.ResumeAsync(lookup, ct: ct)).Action);

        Assert.Equal(JobStatusCode.Paused, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(await SlotAsync(ct), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Idle, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
    }

    [Fact(DisplayName = "Pausing, resuming, or triggering a schedule of a paused recurring job leaves the job paused")]
    public async Task Schedule_verbs_leave_a_paused_recurring_job_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(await SlotAsync(ct), ct: ct)).Action);
        var lookup = Schedule;

        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.PauseAsync(lookup, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.ResumeAsync(lookup, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);

        Assert.Equal(ControlAction.Rejected, (await Operations.Schedules.TriggerNowAsync(lookup, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
    }

    [Fact(DisplayName = "A cancelled recurring job stays cancelled when the worker starts again")]
    public async Task A_cancelled_recurring_job_stays_cancelled_across_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(await SlotAsync(ct), ct: ct)).Action);

        await Runtime.InitializeAsync(ct);

        Assert.Equal(JobStatusCode.Cancelled, (await Jobs.GetStatusAsync(await SlotAsync(ct), ct))?.Status);
    }

    [Fact(
        DisplayName = "A deploy that drops a recurring job's schedules leaves it Idle with nothing due, and their return re-arms the schedule"
    )]
    public async Task Dropped_schedules_leave_the_job_idle_until_they_return()
    {
        var ct = TestContext.Current.CancellationToken;

        // A build without the schedule starts, as a deploy that removed [Schedule] would.
        await using (var dropped = BuildGenerationProvider<UnscheduledPingManifest>(DateTime.UtcNow, "dropped"))
        {
            await dropped.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);
        }
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);
        var idle = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, idle.Status);
        Assert.Null(idle.NextRunAtUtc);
        var orphaned = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Orphaned, orphaned.Status);
        Assert.Null(orphaned.NextEvaluationAtUtc);

        // The schedule comes back with the next deploy, due for the admitter again.
        await Runtime.InitializeAsync(ct);

        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        var back = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, back.Status);
        Assert.NotNull(back.NextEvaluationAtUtc);
    }
}
