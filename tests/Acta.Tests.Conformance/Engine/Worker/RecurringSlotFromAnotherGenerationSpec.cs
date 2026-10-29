using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Worker;

/// <summary>
/// The older generation's manifest: the test manifest with <c>recurring-ping</c> declaring no schedule,
/// the way a build looks before a deploy adds <c>[JobSchedule]</c> to a job it already runs.
/// </summary>
public sealed class UnscheduledPingManifest : IJobManifest
{
    public const string JobName = "recurring-ping";

    public static JobDescriptorManifest Descriptors { get; } =
        new([.. TestJobsManifest.Descriptors.Descriptors.Select(d => d.JobName == JobName ? d with { Schedules = [] } : d)]);
}

/// <summary>
/// A worker learns its recurring slots when it starts, so a slot a newer generation registered later is
/// not among them. The claim carries the row's kind, so the older worker still lands it Idle as a
/// permanent slot instead of running it once and ending it.
/// </summary>
[ConformanceSpec(
    "schedule.slot-from-another-generation",
    "An older generation fires a slot a newer one registered as recurring",
    Area = "Scheduling",
    Contract = "An older worker lands a slot a newer generation registered Idle, as a permanent slot, never finished like a one-shot."
)]
public sealed class RecurringSlotFromAnotherGenerationSpec : ActaRuntimeTestBase<ConformanceFixture, UnscheduledPingManifest>
{
    private const string ScheduleName = "every-5-minutes";

    private static readonly DateTime GenerationA = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime GenerationB = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        services.Configure<JobsOptions>(o => o.ManifestGenerationUtc = GenerationA);
    }

    [Fact(DisplayName = "An older generation fires a slot a newer one registered after it started as a recurring job")]
    public async Task An_older_generation_fires_a_later_registered_slot_as_recurring()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var newGeneration = BuildGenerationProvider<TestJobsManifest>(GenerationB, "generation-b");
        await newGeneration.GetServices<WorkerRuntime>().Single().InitializeAsync(ct);
        var triggered = await Operations.Schedules.TriggerNowAsync(
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
