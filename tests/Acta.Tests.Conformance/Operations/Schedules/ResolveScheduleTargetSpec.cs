using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
// Conformance for <c>ResolveScheduleTarget</c>, the lookup every schedule control verb starts from: the named live
/// schedule with its id, its slot job, its definition, and the database clock, or <c>null</c> when the job or the
/// schedule is absent.
/// </summary>
[ConformanceSpec(
    "resolve-schedule-target.lookup",
    "ResolveScheduleTarget finds a live schedule by its path, null when absent",
    Area = "Scheduling",
    Contract = "ResolveScheduleTarget returns a named live schedule with its id, slot job, definition, and the database clock, or null when the job or schedule is absent."
)]
public sealed class ResolveScheduleTargetSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const string JobName = "recurring-ping";
    private const string ScheduleName = "every-5-minutes";

    [Fact(DisplayName = "A live schedule resolves with its id, its slot job, its definition, and the database clock")]
    public async Task A_live_schedule_resolves_with_its_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct), s => s.ScheduleName == ScheduleName).Config;
        var before = await NowAsync(ct);

        var target = await ResolveAsync(new ScheduleLookup(TestNamespace, JobName, ScheduleName), ct);
        var after = await NowAsync(ct);

        Assert.NotNull(target);
        Assert.Equal(schedule.Id, target.ScheduleId);
        Assert.Equal(slotId, target.JobId);
        Assert.Equal(ScheduleName, target.ScheduleName);
        Assert.InRange(target.NowUtc, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact(DisplayName = "A schedule the slot does not carry, or a job that does not exist, resolves to null")]
    public async Task A_missing_schedule_or_job_resolves_to_null()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await ResolveAsync(new ScheduleLookup(TestNamespace, JobName, "no-such-schedule"), ct));
        Assert.Null(await ResolveAsync(new ScheduleLookup(TestNamespace, "no-such-job", ScheduleName), ct));
    }

    private ValueTask<ResolveScheduleTargetResponse?> ResolveAsync(ScheduleLookup schedule, CancellationToken ct) =>
        Services.GetRequiredService<ResolveScheduleTargetHandler>().HandleAsync(new ResolveScheduleTargetQuery(schedule), ct);

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);
}
