using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// An operator's pause or cancel of a recurring job holds until an operator lifts it. A job pause leaves a
/// next run, which marks it held (<c>ScheduleWalker.IsHeld</c>), and startup and the schedule verbs leave a
/// held or finished slot alone. A slot whose schedules offer no run, a deploy that dropped them included,
/// is Paused with no next run and runs again once a schedule offers one.
/// </summary>
[ConformanceSpec(
    "schedule.job-pause-holds",
    "A paused or cancelled recurring job stays so until an operator lifts it",
    Area = "Scheduling",
    Contract = "A recurring job an operator paused or cancelled keeps that status through worker restarts, schedule edits, and trigger-now.",
    Arrange = "The manifest's recurring job is registered at startup, then paused or cancelled through IJobs, or its schedules dropped by a deploy.",
    Act = "The worker starts again, the schedule's expression is overridden, trigger-now is asked for, and then the job is resumed.",
    Assert = "A paused or cancelled job keeps its status until an operator acts, and a job whose schedules returned runs again."
)]
public abstract class RecurringJobPauseSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private JobLookup Slot => JobLookup.ByDeduplicationKey(TestNamespace, JobName);

    [Fact(DisplayName = "A paused recurring job stays paused when the worker starts again, and a resume makes it Ready")]
    public async Task A_paused_recurring_job_stays_paused_across_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(Slot, ct: ct)).Action);

        // A second startup on the same runtime re-runs registration and the schedule reconcile.
        await Runtime.InitializeAsync(ct);

        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(Slot, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(Slot, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(Slot, ct));
    }

    [Fact(DisplayName = "Pausing and resuming the only schedule of a paused recurring job leaves the job paused")]
    public async Task Pausing_and_resuming_its_only_schedule_leaves_a_paused_job_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(Slot, ct: ct)).Action);
        var lookup = new ScheduleLookup(Slot, ScheduleName);

        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.PauseAsync(lookup, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Operations.Schedules.ResumeAsync(lookup, ct: ct)).Action);

        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(Slot, ct));
        Assert.Equal(ControlAction.Applied, (await Jobs.ResumeAsync(Slot, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, await Jobs.GetStatusAsync(Slot, ct));
    }

    [Fact(DisplayName = "Editing or triggering a schedule of a paused recurring job leaves the job paused")]
    public async Task Schedule_verbs_leave_a_paused_recurring_job_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(Slot, ct: ct)).Action);
        var slotId = (await Jobs.GetJobIdAsync(Slot, ct))!.Value;
        var schedule = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId && s.Name == ScheduleName).ToListAsync(ct));
        var lookup = new ScheduleLookup(Slot, ScheduleName);

        var edited = await Operations.Schedules.UpdateOverridesAsync(lookup, schedule.Version, "*/10 * * * *", null, ct: ct);
        Assert.Equal(ControlAction.Applied, edited.Action);
        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(Slot, ct));

        Assert.Equal(ControlAction.Rejected, (await Operations.Schedules.TriggerNowAsync(lookup, ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, await Jobs.GetStatusAsync(Slot, ct));
    }

    [Fact(DisplayName = "A cancelled recurring job stays cancelled when the worker starts again")]
    public async Task A_cancelled_recurring_job_stays_cancelled_across_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(Slot, ct: ct)).Action);

        await Runtime.InitializeAsync(ct);

        Assert.Equal(JobStatusCode.Cancelled, await Jobs.GetStatusAsync(Slot, ct));
    }

    [Fact(DisplayName = "A deploy that drops a recurring job's schedules pauses it, and their return makes it Ready")]
    public async Task Dropped_schedules_pause_the_job_until_they_return()
    {
        var ct = TestContext.Current.CancellationToken;

        // A build without the schedule starts, as a deploy that removed [JobSchedule] would.
        await using (var dropped = BuildGenerationProvider<UnscheduledPingManifest>(DateTime.UtcNow, "dropped"))
        {
            await dropped.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);
        }
        var slotId = (await Jobs.GetJobIdAsync(Slot, ct))!.Value;
        var idle = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Paused, idle.Status);
        Assert.Null(idle.NextRunAtUtc);

        // The schedule comes back with the next deploy.
        await Runtime.InitializeAsync(ct);

        var back = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, back.Status);
        Assert.NotNull(back.NextRunAtUtc);
    }
}
