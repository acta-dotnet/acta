using Acta.Relational.Entities;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// An operator's pause of a recurring job holds until an operator resumes it. Startup recomputes a slot's
/// status from its schedules and the schedule verbs recompute it after every change, and neither may
/// read a job pause as a slot with nothing left to run.
/// </summary>
[ConformanceSpec(
    "schedule.job-pause-holds",
    "A paused recurring job stays paused until an operator resumes it",
    Area = "Scheduling",
    Contract = "A recurring job paused by an operator stays Paused through worker restarts, schedule edits, and trigger-now until a job resume.",
    Arrange = "The manifest's recurring job is registered at startup and paused through IJobs.",
    Act = "The worker starts again, the schedule's expression is overridden, trigger-now is asked for, and then the job is resumed.",
    Assert = "The job stays Paused through the restart and the edit, trigger-now is rejected, and only the resume makes it Ready."
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
}
