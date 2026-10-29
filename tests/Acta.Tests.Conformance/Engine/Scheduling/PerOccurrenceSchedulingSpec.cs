using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Succeeds at once; a per-occurrence root with nothing to do.</summary>
internal static class RootHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Throws on every attempt, so a root fails for good at MaxAttempts = 1.</summary>
internal static class FailingRootHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => throw new InvalidOperationException("the root fails");
}

/// <summary>PerOccurrence definitions, one per fact, each with one five-minute schedule of the declared capacity.</summary>
public sealed class PerOccurrenceManifest : IJobManifest
{
    public const string WaitingJobName = "roots-wait";
    public const string DroppingJobName = "roots-drop";
    public const string ControlledJobName = "roots-control";
    public const string TriggeredJobName = "roots-trigger";
    public const string FailingJobName = "roots-fail";
    public const string LostJobName = "roots-lost";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(WaitingJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(DroppingJobName, 1, ScheduleFullBehaviorCode.Drop, fails: false),
            Descriptor(ControlledJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(TriggeredJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(FailingJobName, 2, ScheduleFullBehaviorCode.Wait, fails: true),
            Descriptor(LostJobName, 2, ScheduleFullBehaviorCode.Wait, fails: true),
        ]);

    private static JobDescriptor Descriptor(string jobName, int maxOutstanding, ScheduleFullBehaviorCode fullBehavior, bool fails) =>
        new(
            JobName: jobName,
            HandlerType: fails ? typeof(FailingRootHandler) : typeof(RootHandler),
            MethodName: "Run",
            InputType: typeof(NoInput),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            OutputPayloadFormat: null,
            Priority: JobPriorityCode.Normal,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: AlertProfileCode.None,
            FiringFailurePolicy: fails ? FiringFailurePolicyCode.PauseScheduling : FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Invoker: fails
                ? static async (_, _, ctx, ct) =>
                {
                    await FailingRootHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                }
                : static async (_, _, ctx, ct) =>
                {
                    await RootHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
            DeserializeInput: static (_, _) => new NoInput(),
            SerializeOutput: null
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
                    MaxOutstanding: maxOutstanding,
                    FullBehavior: fullBehavior
                ),
            ],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
        };
}

/// <summary>
/// A PerOccurrence definition's schedule admits each occurrence as a fresh ordinary root charged to the schedule: the
/// root carries the schedule as provenance and a one-contributor envelope, every terminalization releases the charge
/// once, a full schedule waits or drops by its declared behavior, a restart re-charges, a manual trigger is charged,
/// and a root that fails for good applies the firing failure policy.
/// </summary>
[ConformanceSpec(
    "schedule.per-occurrence",
    "A PerOccurrence schedule admits roots charged to it and settles them once",
    Area = "Scheduling",
    Contract = "A PerOccurrence schedule admits each occurrence as a root charged to it, releases the charge once per terminalization, and waits or drops when full."
)]
public sealed class PerOccurrenceSchedulingSpec : ActaRuntimeTestBase<ConformanceFixture, PerOccurrenceManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Clock = new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(
        DisplayName = "An admitted occurrence is a Ready root with the schedule as provenance, one charge, and its own envelope, settled on completion"
    )]
    public async Task An_occurrence_becomes_a_root_charged_to_its_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;

        await AdmitAtAsync(occurrence, ct);

        var root = Assert.Single(await RootsAsync(schedule.Id, ct));
        Assert.Equal(JobKindCode.Ordinary, root.Kind);
        Assert.Equal(ScheduleAdmitter.OccurrenceKey(schedule.Id, occurrence), root.DeduplicationKey);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(root.Id, ct)).Status);
        var envelope = await EnvelopeAsync(root.Id, ct);
        Assert.Equal(CheckpointStatusCode.Pending, envelope.Status);
        Assert.Equal(FiringEnvelope.ScheduledKind, envelope.Firing.Kind);
        Assert.Equal(occurrence, Assert.Single(envelope.Firing.Contributors).OccurrenceAtUtc);
        (_, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        Assert.Equal(1, state.OutstandingCount);
        Assert.Equal(occurrence.AddMinutes(5), state.NextOccurrenceAtUtc);
        Assert.Equal(
            PerOccurrenceManifest.ScheduleName,
            EventDetail(await ReadSingleEventAsync(root.Id, EventCode.ScheduleAdmitted, ct), "schedule")
        );

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(root.Id, ct));
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Succeeded, (await EnvelopeAsync(root.Id, ct)).Status);
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct)).State.OutstandingCount);
    }

    [Fact(DisplayName = "At MaxOutstanding a Wait schedule parks until a settlement wakes it, then admits the waiting occurrence")]
    public async Task A_full_schedule_waits_for_a_settlement()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        var first = Assert.Single(await RootsAsync(schedule.Id, ct));

        await AdmitAtAsync(occurrence.AddMinutes(5), ct);

        Assert.Single(await RootsAsync(schedule.Id, ct));
        (_, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        Assert.Equal(EvaluationStatusCode.WaitingForCapacity, state.EvaluationStatus);
        Assert.Null(state.NextEvaluationAtUtc);
        Assert.Equal(occurrence.AddMinutes(5), state.NextOccurrenceAtUtc);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(first.Id, ct));
        (_, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        Assert.Equal(EvaluationStatusCode.Scheduled, state.EvaluationStatus);
        Assert.NotNull(state.NextEvaluationAtUtc);

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(2, (await RootsAsync(schedule.Id, ct)).Count);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct)).State.OutstandingCount);
    }

    [Fact(DisplayName = "At MaxOutstanding a Drop schedule skips the occurrence with the reason capacity and moves on")]
    public async Task A_full_schedule_drops_the_occurrence()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.DroppingJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);

        await AdmitAtAsync(occurrence.AddMinutes(5), ct);

        Assert.Single(await RootsAsync(schedule.Id, ct));
        (_, state) = await ScheduleAsync(PerOccurrenceManifest.DroppingJobName, ct);
        Assert.Equal(EvaluationStatusCode.Scheduled, state.EvaluationStatus);
        Assert.Equal(occurrence.AddMinutes(10), state.NextOccurrenceAtUtc);
        Assert.Equal(1, state.OutstandingCount);
        var skipped = Assert.Single(await DefinitionEventsAsync(schedule.DefinitionId, EventCode.ScheduleSkipped, ct));
        Assert.Equal("capacity", EventDetail(skipped, "reason"));
    }

    [Fact(DisplayName = "A cancel releases the charge, a restart takes it again, and a restart into a full schedule is Rejected")]
    public async Task Cancel_and_restart_keep_the_charge_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.ControlledJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        var first = Assert.Single(await RootsAsync(schedule.Id, ct));

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(JobLookup.ById(first.Id), ct: ct)).Action);
        Assert.Equal(CheckpointStatusCode.Cancelled, (await EnvelopeAsync(first.Id, ct)).Status);
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.ControlledJobName, ct)).State.OutstandingCount);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(JobLookup.ById(first.Id), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(first.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Pending, (await EnvelopeAsync(first.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.ControlledJobName, ct)).State.OutstandingCount);
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(first.Id, ct));
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.ControlledJobName, ct)).State.OutstandingCount);

        // The next occurrence fills the schedule, so the finished first root cannot take the capacity again.
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        Assert.Equal(2, (await RootsAsync(schedule.Id, ct)).Count);
        Assert.Equal(ControlAction.Rejected, (await Jobs.RestartAsync(JobLookup.ById(first.Id), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Succeeded, (await ReadJobAsync(first.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.ControlledJobName, ct)).State.OutstandingCount);
    }

    [Fact(DisplayName = "A manual trigger admits a charged root with an empty envelope, and a full schedule rejects it")]
    public async Task A_manual_trigger_admits_a_charged_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, _) = await ScheduleAsync(PerOccurrenceManifest.TriggeredJobName, ct);
        var lookup = new ScheduleLookup(TestNamespace, PerOccurrenceManifest.TriggeredJobName, PerOccurrenceManifest.ScheduleName);

        Assert.Equal(ControlAction.Applied, (await Schedules.TriggerNowAsync(lookup, reasonMessage: "now", actor: "ops", ct: ct)).Action);

        var root = Assert.Single(await RootsAsync(schedule.Id, ct));
        Assert.StartsWith("sys.manual.", root.DeduplicationKey, StringComparison.Ordinal);
        var envelope = await EnvelopeAsync(root.Id, ct);
        Assert.Equal(FiringEnvelope.ManualKind, envelope.Firing.Kind);
        Assert.Empty(envelope.Firing.Contributors);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.TriggeredJobName, ct)).State.OutstandingCount);
        var triggered = await ReadSingleEventAsync(root.Id, EventCode.ScheduleTriggered, ct);
        Assert.Equal("now", triggered.EventMessage);
        Assert.Equal(PerOccurrenceManifest.ScheduleName, EventDetail(triggered, "schedule"));

        Assert.Equal(ControlAction.Rejected, (await Schedules.TriggerNowAsync(lookup, ct: ct)).Action);
        Assert.Single(await RootsAsync(schedule.Id, ct));
    }

    [Fact(
        DisplayName = "A root that fails for good settles its envelope Failed, releases the charge, and pauses the schedules under PauseScheduling"
    )]
    public async Task A_failed_root_applies_the_firing_failure_policy()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.FailingJobName, ct);
        await AdmitAtAsync(state.NextOccurrenceAtUtc!.Value, ct);
        var root = Assert.Single(await RootsAsync(schedule.Id, ct));

        Assert.Equal(RunOnceStatus.Failed, await Runtime.RunOnceAsync(root.Id, ct));

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Failed, (await EnvelopeAsync(root.Id, ct)).Status);
        (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.FailingJobName, ct);
        Assert.Equal(0, state.OutstandingCount);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal("Paused by the firing failure policy: the last firing failed for good.", schedule.PauseMessage);
        Assert.Null(state.NextEvaluationAtUtc);
        Assert.Equal("firing-failed", EventDetail(await ReadSingleEventAsync(root.Id, EventCode.SchedulePaused, ct), "reason"));
    }

    [Fact(DisplayName = "A lost attempt that spends the budget settles the root on reclaim as a completion would")]
    public async Task A_reclaim_that_spends_the_budget_settles_the_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.LostJobName, ct);
        await AdmitAtAsync(state.NextOccurrenceAtUtc!.Value, ct);
        var root = Assert.Single(await RootsAsync(schedule.Id, ct));

        // The root's only attempt is in flight on a lease that lapsed.
        Assert.Equal(
            1,
            await Db.From<JobStateEntity>()
                .Where(r => r.Id == root.Id)
                .UpdateOnlyAsync(
                    () =>
                        new JobStateEntity
                        {
                            Status = JobStatusCode.Executing,
                            LeasedByWorkerId = workerId,
                            LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5),
                        },
                    ct
                )
        );

        await ChaosSpecHelpers.ReclaimAsync(Services, ns, ct);

        Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Failed, (await EnvelopeAsync(root.Id, ct)).Status);
        (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.LostJobName, ct);
        Assert.Equal(0, state.OutstandingCount);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
    }

    private async Task AdmitAtAsync(DateTime nowUtc, CancellationToken ct)
    {
        Clock.AdvanceTo(nowUtc);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
    }

    private async Task<(ScheduleEntity Config, ScheduleStateEntity State)> ScheduleAsync(string jobName, CancellationToken ct)
    {
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var definition = await Db.From<DefinitionEntity>()
            .Where(d => d.NamespaceId == ns && d.DefinitionName == jobName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(definition);
        var config = await Db.From<ScheduleEntity>()
            .Where(s => s.DefinitionId == definition.Id && s.ScheduleName == PerOccurrenceManifest.ScheduleName)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(config);
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == config.Id).SingleOrDefaultAsync(ct);
        Assert.NotNull(state);
        return (config, state);
    }

    private async Task<IReadOnlyList<JobEntity>> RootsAsync(int scheduleId, CancellationToken ct) =>
        [.. (await Db.From<JobEntity>().Where(j => j.ScheduleId == scheduleId).ToListAsync(ct)).OrderBy(j => j.Id)];

    private async Task<(CheckpointStatusCode? Status, FiringEnvelope Firing)> EnvelopeAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(checkpoint);
        var firing = FiringEnvelope.FromJson(checkpoint.Value);
        Assert.NotNull(firing);
        return (checkpoint.Status, firing);
    }

    private async Task<IReadOnlyList<EventEntity>> DefinitionEventsAsync(int definitionId, EventCode code, CancellationToken ct)
    {
        long entityId = definitionId;
        return
        [
            .. (
                await Db.From<EventEntity>().Where(e => e.EntityCode == EntityCode.Definition && e.EntityId == entityId).ToListAsync(ct)
            ).Where(e => e.EventCode == code),
        ];
    }
}
