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
/// the older worker still fires it as a recurring job instead of running it once and ending it. An
/// ordinary job that took that key first is never adopted as the slot: the newer generation refuses
/// to start instead of rewriting it.
/// </summary>
[ConformanceSpec(
    "schedule.slot-from-another-generation",
    "An older generation fires a slot a newer one registered as recurring",
    Area = "Scheduling",
    Contract = "An older worker fires a slot a newer generation registered as recurring, and no registration adopts an ordinary job holding the key.",
    Arrange = "The older generation starts without the schedule, and in one fact ordinary laned jobs already hold the slot's key.",
    Act = "A newer generation registers the schedule, and the older generation claims and runs the slot.",
    Assert = "The slot is Ready again with its next run ahead, and the newer generation refuses to start over an ordinary job, leaving its lane."
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
        var slotId = (await Jobs.GetJobIdAsync(Slot, ct))!.Value;
        // Against the triggered due instant, not the clock: a run that ends just before a five-minute
        // boundary re-arms onto that boundary, which a check against now would read as not re-armed.
        var due = (await ReadJobAsync(slotId, ct)).NextRunAtUtc;

        Assert.Equal(RunOnceOutcome.Completed, await Runtime.RunOnceAsync(TestNamespace, ct));

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.True(slot.NextRunAtUtc > due);
    }

    [Fact(
        DisplayName = "A deploy that schedules a job refuses to start while an ordinary job holds its key, and leaves that job's lane as it was"
    )]
    public async Task A_schedule_never_adopts_an_ordinary_job_holding_its_key()
    {
        var ct = TestContext.Current.CancellationToken;

        // Before the schedule exists: two ordinary jobs in one lane, the second under the key a slot will use.
        var head = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, UnscheduledPingManifest.JobName, JobPayload.None, Lane: "l", DeduplicationKey: "other"),
            ct
        );
        var behind = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(
                TestNamespace,
                UnscheduledPingManifest.JobName,
                JobPayload.None,
                Lane: "l",
                DeduplicationKey: UnscheduledPingManifest.JobName
            ),
            ct
        );
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(behind.JobId, ct)).Status);

        await using var newGeneration = BuildGenerationProvider<TestJobsManifest>(GenerationB, "generation-b");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await newGeneration.GetServices<WorkerRuntime>().Single().InitializeAsync(ct)
        );

        Assert.Contains(UnscheduledPingManifest.JobName, refused.Message, StringComparison.Ordinal);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(head.JobId, ct)).Status);
        Assert.Equal(JobStatusCode.Blocked, (await ReadJobAsync(behind.JobId, ct)).Status);
        Assert.Empty(await Db.From<Acta.Relational.Entities.JobSchedule>().Where(s => s.JobId == behind.JobId).ToListAsync(ct));
    }
}
