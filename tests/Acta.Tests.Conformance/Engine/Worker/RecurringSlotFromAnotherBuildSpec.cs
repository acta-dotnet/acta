using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The earlier build's manifest: <c>recurring-ping</c> declaring no schedule, the way a build looks before a deploy
/// adds <c>[Schedule]</c> to a job it already runs.
/// </summary>
public sealed class UnscheduledPingManifest : IJobManifest
{
    public const string JobName = "recurring-ping";

    public static JobDescriptorManifest Descriptors { get; } =
        new([.. TestJobsManifest.Descriptors.Descriptors.Where(d => d.JobName == JobName).Select(d => d with { Schedules = [] })]);
}

/// <summary>
/// A worker learns its recurring slots when it starts, so a slot another build's deploy wrote later is
/// not among them. The claim carries the row's kind, so the earlier worker still lands it Idle as a
/// permanent slot instead of running it once and ending it.
/// </summary>
[ConformanceSpec(
    "schedule.slot-from-another-build",
    "A worker fires a slot another build deployed after it started as recurring",
    Area = "Scheduling",
    Contract = "A worker lands a slot another build deployed after it started Idle, as a permanent slot, never finished like a one-shot."
)]
[Deploys("recurring-ping")]
public sealed class RecurringSlotFromAnotherBuildSpec : ActaRuntimeTestBase<ConformanceFixture, UnscheduledPingManifest>
{
    private const string ScheduleName = "every-5-minutes";

    [Fact(DisplayName = "A worker fires a slot another build deployed after it started as a recurring job")]
    public async Task A_worker_fires_a_later_deployed_slot_as_recurring()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var laterBuild = BuildProvider<JobsOf<RecurringSlotFromAnotherBuildSpec>>("later-build");
        await CatalogTestOps.DeployAsync(laterBuild, ct);
        var triggered = await Operations.Schedules.TriggerAsync(
            new ScheduleLookup(TestNamespace, UnscheduledPingManifest.JobName, ScheduleName),
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, triggered.Action);
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, UnscheduledPingManifest.JobName, ct);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(TestNamespace, ct));

        // The firing settled and the slot is back where admission finds it, not finished like a one-shot.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
    }
}
