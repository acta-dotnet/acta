using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>
/// PerOccurrence definitions that differ only in their attempt budget, each with one five-minute Wait schedule of
/// capacity one. The firing failure policy is Continue, since under PauseScheduling a root that fails for good parks its
/// schedule over the wake.
/// </summary>
public sealed class PerOccurrenceChargeManifest : IJobManifest
{
    public const string TwoAttemptsJobName = "roots-two-attempts";
    public const string OneAttemptJobName = "roots-one-attempt";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([Descriptor(TwoAttemptsJobName, maxAttempts: 2), Descriptor(OneAttemptJobName, maxAttempts: 1)]);

    private static JobDescriptor Descriptor(string jobName, int maxAttempts) =>
        new(
            JobName: jobName,
            HandlerType: typeof(RootHandler),
            MethodName: "Run",
            InputType: typeof(NoInput),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: maxAttempts,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Invoker: static async (_, _, ctx, ct) =>
            {
                await RootHandler.Run(ctx, ct);
                return new JobHandlerOutput(false, null);
            },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeResult: null
        )
        {
            Schedules =
            [
                new ScheduleDescriptor(
                    JobName: jobName,
                    ScheduleName: ScheduleName,
                    Expression: "*/5 * * * *",
                    TimeZoneId: null,
                    MisfireStrategy: MisfireStrategyCode.Skip,
                    ExpressionKind: ScheduleExpressionKindCode.Cron,
                    Description: null,
                    Environments: [],
                    MaxOutstanding: 1,
                    FullBehavior: ScheduleFullBehaviorCode.Wait
                ),
            ],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// A charged root holds its schedule's capacity while it can still run: a lost attempt with attempts left keeps the
/// charge, a reclaim that spends the budget or a cancel releases it once and wakes a schedule waiting for capacity, a
/// replayed landing releases nothing, and a finished root is not restarted into an orphaned schedule or a retired
/// definition's.
/// </summary>
[ConformanceSpec(
    "schedule.per-occurrence-charge",
    "A charged root holds its schedule's capacity until it finishes",
    Area = "Scheduling",
    Contract = "A root keeps its charge while it can run again, releases it once when it finishes, waking a waiting schedule, and restarts only into a live schedule."
)]
public sealed class PerOccurrenceChargeSpec : ActaRuntimeTestBase<ConformanceFixture, PerOccurrenceChargeManifest>
{
    private const int LeaseTtlSeconds = 30;

    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(
        DisplayName = "A charged root reclaimed with attempts left keeps its charge and its schedule parked, and its next attempt releases the charge once, replayed or not"
    )]
    public async Task A_reclaim_with_attempts_left_keeps_the_charge()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var (schedule, root, occurrence) = await ParkAtCapacityAsync(PerOccurrenceChargeManifest.TwoAttemptsJobName, ct);

        var lost = await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, root.Id, ct);
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Pending, await EnvelopeStatusAsync(root.Id, ct));
        // Read before the pass, which would park again a schedule the reclaim had woken.
        await AssertParkedAsync(schedule.Id, ct);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Single(await RootsAsync(schedule.Id, ct));
        await AssertParkedAsync(schedule.Id, ct);

        var replacement = await ClaimAsync(root.Id, workerId, ct);
        Assert.Equal(lost.ExecutionNumber + 1, replacement.ExecutionNumber);
        var landing = CompletionTestOps.Landing(replacement);
        Assert.Equal(CompleteExecutionsAction.Completed, (await Execution.CompleteAsync(workerId, landing, ct)).Action);
        var wokenAt = (await AssertWokenAsync(schedule.Id, occurrence, ct)).NextEvaluationAtUtc!.Value;
        // The wake is stamped on the database clock, which may have passed the occurrence this clock stands at.
        Clock.AdvanceTo(wokenAt > Clock.UtcNow ? wokenAt : Clock.UtcNow);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(2, (await RootsAsync(schedule.Id, ct)).Count);
        var admitted = await StateAsync(schedule.Id, ct);
        Assert.Equal(1, admitted.OutstandingCount);

        // The worker writes the landing again, as it does when the first write's answer was lost.
        Assert.Equal(CompleteExecutionsAction.AlreadyTerminal, (await Execution.CompleteAsync(workerId, landing, ct)).Action);
        var replayed = await StateAsync(schedule.Id, ct);
        Assert.Equal((admitted.OutstandingCount, admitted.Version), (replayed.OutstandingCount, replayed.Version));
        await ScheduleTestOps.AssertChargeIsPendingEnvelopesAsync(Db, schedule.Id, ct);
    }

    [Fact(DisplayName = "A charged root reclaimed with its attempts spent releases the charge and wakes a schedule waiting for capacity")]
    public async Task A_reclaim_that_spends_the_budget_wakes_the_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var (schedule, root, occurrence) = await ParkAtCapacityAsync(PerOccurrenceChargeManifest.OneAttemptJobName, ct);

        await ChaosSpecHelpers.StrandExecutingAsync(Services, ns, workerId, root.Id, ct);
        Assert.Equal(1, (await RecoverySweep.ReclaimAtLeastOneAsync(Services, ns, ct)).Reclaimed);

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Failed, await EnvelopeStatusAsync(root.Id, ct));
        await AssertWokenAsync(schedule.Id, occurrence, ct);
    }

    [Fact(DisplayName = "A cancel of a charged root releases the charge and wakes a schedule waiting for capacity")]
    public async Task A_cancel_wakes_the_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, root, occurrence) = await ParkAtCapacityAsync(PerOccurrenceChargeManifest.OneAttemptJobName, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(new JobRef(root.JobRef), ct: ct)).Action);

        Assert.Equal(CheckpointStatusCode.Cancelled, await EnvelopeStatusAsync(root.Id, ct));
        await AssertWokenAsync(schedule.Id, occurrence, ct);
    }

    [Fact(DisplayName = "A finished charged root is not restarted into an orphaned schedule, though the schedule has capacity free")]
    public async Task A_restart_into_an_orphaned_schedule_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, root) = await FinishedRootAsync(ct);

        // The definition's schedules register again without this one, as a deploy that dropped it does.
        await ScheduleTestOps.RegisterAsync(
            Services,
            [
                new DefinitionSchedules(
                    Runtime.RegisteredNamespaceIds[TestNamespace],
                    schedule.DefinitionId,
                    0,
                    ReadOnlyMemory<byte>.Empty,
                    ScheduledDeliveryCode.PerOccurrence,
                    []
                ),
            ],
            ct
        );

        await AssertNotRestartedAsync(root, ScheduleStatusCode.Orphaned, ct);
    }

    [Fact(
        DisplayName = "A finished charged root is not restarted into a retired definition's schedule, though the schedule has capacity free"
    )]
    public async Task A_restart_into_a_retired_definition_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, root) = await FinishedRootAsync(ct);

        var definition = await Db.From<DefinitionEntity>().Where(d => d.Id == schedule.DefinitionId).SingleOrDefaultAsync(ct);
        var retire = await Operations.Definitions.RetireAsync(
            TestNamespace,
            PerOccurrenceChargeManifest.OneAttemptJobName,
            definition!.Version,
            "retired with its root finished",
            "ops",
            ct
        );
        Assert.Equal(ControlAction.Applied, retire.Action);

        // A retire leaves the schedule Active: what refuses the restart is the retired definition.
        await AssertNotRestartedAsync(root, ScheduleStatusCode.Active, ct);
    }

    /// <summary>
    /// Fills the schedule with a triggered root, then reaches its occurrence, which waits for capacity; answers the
    /// schedule, the root, and the waiting occurrence.
    /// </summary>
    private async Task<(ScheduleEntity Schedule, JobEntity Root, DateTime Occurrence)> ParkAtCapacityAsync(
        string jobName,
        CancellationToken ct
    )
    {
        var (schedule, state) = await ScheduleAsync(jobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        var root = await TriggerAsync(jobName, ct);
        Clock.AdvanceTo(occurrence);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        Assert.Single(await RootsAsync(schedule.Id, ct));
        await AssertParkedAsync(schedule.Id, ct);
        return (schedule, root, occurrence);
    }

    /// <summary>The schedule holds its one charge and waits for capacity, with no evaluation due.</summary>
    private async Task AssertParkedAsync(int scheduleId, CancellationToken ct)
    {
        var state = await StateAsync(scheduleId, ct);
        Assert.Equal(1, state.OutstandingCount);
        Assert.Equal(ScheduleEvaluationStatusCode.WaitingForCapacity, state.EvaluationStatus);
        Assert.Null(state.NextEvaluationAtUtc);
    }

    /// <summary>A triggered root of the one-attempt schedule that ran to success, leaving the schedule with nothing outstanding.</summary>
    private async Task<(ScheduleEntity Schedule, JobEntity Root)> FinishedRootAsync(CancellationToken ct)
    {
        var (schedule, _) = await ScheduleAsync(PerOccurrenceChargeManifest.OneAttemptJobName, ct);
        var root = await TriggerAsync(PerOccurrenceChargeManifest.OneAttemptJobName, ct);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(root.Id, ct));
        return (schedule, root);
    }

    /// <summary>The root's restart is Rejected with capacity free, and the root, its envelope, and the charge stay as they were.</summary>
    private async Task AssertNotRestartedAsync(JobEntity root, ScheduleStatusCode scheduleStatus, CancellationToken ct)
    {
        var (schedule, before) = await ScheduleAsync(PerOccurrenceChargeManifest.OneAttemptJobName, ct);
        Assert.Equal(scheduleStatus, schedule.Status);
        Assert.True(before.OutstandingCount < schedule.MaxOutstanding, "the schedule has no capacity free");

        Assert.Equal(ControlAction.Rejected, (await Jobs.RestartAsync(new JobRef(root.JobRef), ct: ct)).Action);

        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Succeeded, await EnvelopeStatusAsync(root.Id, ct));
        Assert.Equal(before.OutstandingCount, (await StateAsync(schedule.Id, ct)).OutstandingCount);
        await ScheduleTestOps.AssertChargeIsPendingEnvelopesAsync(Db, schedule.Id, ct);
    }

    /// <summary>
    /// The schedule's one root has finished: its charge is gone, and the schedule is due now for the occurrence that
    /// waited, not at some later time or for a later occurrence.
    /// </summary>
    private async Task<ScheduleStateEntity> AssertWokenAsync(int scheduleId, DateTime occurrence, CancellationToken ct)
    {
        var state = await StateAsync(scheduleId, ct);
        Assert.Equal(0, state.OutstandingCount);
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, state.EvaluationStatus);
        Assert.Equal(occurrence, state.NextOccurrenceAtUtc);
        Assert.True(state.NextEvaluationAtUtc <= DateTime.UtcNow.AddSeconds(30), $"Woken for {state.NextEvaluationAtUtc:O}, not for now.");
        await ScheduleTestOps.AssertChargeIsPendingEnvelopesAsync(Db, scheduleId, ct);
        return state;
    }

    private async Task<JobEntity> TriggerAsync(string jobName, CancellationToken ct)
    {
        var lookup = new ScheduleLookup(TestNamespace, jobName, PerOccurrenceChargeManifest.ScheduleName);
        var triggered = await Operations.Schedules.TriggerAsync(lookup, ct: ct);
        Assert.Equal(ControlAction.Applied, triggered.Action);
        var jobRef = triggered.JobRef!.Value.Value;
        var root = await Db.From<JobEntity>().Where(j => j.JobRef == jobRef).SingleOrDefaultAsync(ct);
        Assert.NotNull(root);
        return root;
    }

    private async Task<ClaimedJob> ClaimAsync(long jobId, int workerId, CancellationToken ct) =>
        Assert.Single(await Execution.ClaimJobAsync(Runtime.RegisteredNamespaceIds[TestNamespace], workerId, LeaseTtlSeconds, jobId, ct));

    private async Task<(ScheduleEntity Config, ScheduleStateEntity State)> ScheduleAsync(string jobName, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == jobName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(definition);
        var config = await Db.From<ScheduleEntity>()
            .Where(s => s.DefinitionId == definition.Id && s.ScheduleName == PerOccurrenceChargeManifest.ScheduleName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(config);
        return (config, await StateAsync(config.Id, ct));
    }

    private async Task<ScheduleStateEntity> StateAsync(int scheduleId, CancellationToken ct)
    {
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == scheduleId).SingleOrDefaultAsync(ct);
        Assert.NotNull(state);
        return state;
    }

    private async Task<IReadOnlyList<JobEntity>> RootsAsync(int scheduleId, CancellationToken ct) =>
        await Db.From<JobEntity>().Where(j => j.ScheduleId == scheduleId).ToListAsync(ct);

    private async Task<CheckpointStatusCode?> EnvelopeStatusAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(checkpoint);
        return checkpoint.Status;
    }
}
