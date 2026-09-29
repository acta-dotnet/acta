using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

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
/// not among them. The slot's deduplication key is its job name and its live schedules confirm it, so
/// the older worker still fires it as a recurring job instead of running it once and ending it.
/// </summary>
[ConformanceSpec(
    "schedule.slot-from-another-generation",
    "An older generation fires a slot a newer one registered as recurring",
    Area = "Scheduling",
    Contract = "A worker that did not register a recurring slot at startup still fires it as a recurring job when it claims it.",
    Arrange = "The older generation starts without the schedule, then a newer generation registers it and the slot is triggered now.",
    Act = "The older generation claims and runs the slot.",
    Assert = "The slot is Ready again with its next run ahead, not Succeeded."
)]
public abstract class RecurringSlotFromAnotherGenerationSpec<TFixture> : ActaRuntimeTestBase<TFixture, UnscheduledPingManifest>
    where TFixture : IConformanceFixture, new()
{
    private const string ScheduleName = "every-5-minutes";

    private static readonly DateTime GenerationA = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime GenerationB = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private JobLookup Slot => JobLookup.ByDeduplicationKey(TestNamespace, UnscheduledPingManifest.JobName);

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
        var triggered = await Operations.Schedules.TriggerNowAsync(new ScheduleLookup(Slot, ScheduleName), ct: ct);
        Assert.Equal(ControlAction.Applied, triggered.Action);

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, ct));

        var slot = await ReadJobAsync((await Jobs.GetJobIdAsync(Slot, ct))!.Value, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.True(slot.NextRunAtUtc > DateTime.UtcNow);
    }
}
