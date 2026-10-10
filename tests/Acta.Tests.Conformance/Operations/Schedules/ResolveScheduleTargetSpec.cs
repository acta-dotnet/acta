using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for <c>ResolveScheduleTarget</c>, the lookup every schedule control verb starts from: the named live
/// schedule with its recurring slot job, the database clock, and the slot's live schedules, or <c>null</c> when the job
/// or the schedule is absent.
/// </summary>
[ConformanceSpec(
    "resolve-schedule-target.lookup",
    "ResolveScheduleTarget finds a live schedule and its slot job, null when absent",
    Area = "Scheduling",
    Contract = "ResolveScheduleTarget returns a named live schedule with its slot job, the database clock, and the slot's live set, or null when the job or schedule is absent."
)]
public sealed class ResolveScheduleTargetSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    private JobLookup Slot => JobLookup.ByDeduplicationKey(TestNamespace, JobName);

    [Fact(DisplayName = "A live schedule resolves with its slot job, the database clock, and the slot's live schedules")]
    public async Task A_live_schedule_resolves_with_its_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = (await Jobs.GetJobIdAsync(Slot, ct))?.JobId;
        var before = await NowAsync(ct);

        var target = await ResolveAsync(new ScheduleLookup(Slot, ScheduleName), ct);
        var after = await NowAsync(ct);

        Assert.NotNull(target);
        Assert.Equal(slotId, target.JobId);
        Assert.Equal(ScheduleName, target.Schedule.Name);
        Assert.Equal([ScheduleName], target.Live.Select(s => s.Name));
        Assert.InRange(target.NowUtc, before.AddSeconds(-1), after.AddSeconds(1));
        // Paused indefinitely, the slot's only schedule leaves the slot no next run.
        Assert.Null(target.SlotNextRunWith(s => s with { Status = ScheduleStatusCode.Paused, PausedUntilUtc = null }));
    }

    [Fact(DisplayName = "A schedule the slot does not carry, or a job that does not exist, resolves to null")]
    public async Task A_missing_schedule_or_job_resolves_to_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await ResolveAsync(new ScheduleLookup(Slot, "no-such-schedule"), ct));
        Assert.Null(await ResolveAsync(new ScheduleLookup(JobLookup.ByRef(JobRef.New()), ScheduleName), ct));
    }

    private ValueTask<ResolveScheduleTargetResponse?> ResolveAsync(ScheduleLookup schedule, CancellationToken ct) =>
        Services.GetRequiredService<ResolveScheduleTargetHandler>().HandleAsync(new ResolveScheduleTargetQuery(schedule), ct);

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
}
