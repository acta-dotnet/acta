using Acta.Tests.Conformance.Contracts;
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

/// <summary>Waits for a signal nobody raises, so a root suspends on its first attempt.</summary>
internal static class WaitingRootHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => ctx.WaitSignalAsync("go", ct);
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
    public const string HeldJobName = "roots-held";
    public const string DeadlineJobName = "roots-deadline";
    public const string StaleJobName = "roots-stale";
    public const string CancelledJobName = "roots-cancel";
    public const string RetiredJobName = "roots-retire";
    public const string SettledRetireJobName = "roots-settled-retire";
    public const string SettledCancelJobName = "roots-settled-cancel";
    public const string PendingRestartJobName = "roots-pending-restart";
    public const string ReadmittedJobName = "roots-readmit";
    public const string BesideJobName = "roots-beside";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            Descriptor(WaitingJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(DroppingJobName, 1, ScheduleFullBehaviorCode.Drop, fails: false),
            Descriptor(ControlledJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(TriggeredJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(FailingJobName, 2, ScheduleFullBehaviorCode.Wait, fails: true),
            Descriptor(LostJobName, 2, ScheduleFullBehaviorCode.Wait, fails: true),
            Descriptor(HeldJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(DeadlineJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false, deadlineSeconds: 60),
            Descriptor(StaleJobName, 2, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(CancelledJobName, 2, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(RetiredJobName, 3, ScheduleFullBehaviorCode.Wait, fails: false, waits: true),
            Descriptor(SettledRetireJobName, 2, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(SettledCancelJobName, 2, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(PendingRestartJobName, 2, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(ReadmittedJobName, 3, ScheduleFullBehaviorCode.Wait, fails: false),
            Descriptor(BesideJobName, 1, ScheduleFullBehaviorCode.Wait, fails: false),
        ]);

    private static JobDescriptor Descriptor(
        string jobName,
        int maxOutstanding,
        ScheduleFullBehaviorCode fullBehavior,
        bool fails,
        int? deadlineSeconds = null,
        bool waits = false
    ) =>
        new(
            JobName: jobName,
            HandlerType: fails ? typeof(FailingRootHandler)
                : waits ? typeof(WaitingRootHandler)
                : typeof(RootHandler),
            MethodName: "Run",
            InputType: typeof(NoInput),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.None,
            ResultPayloadFormat: null,
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
                : waits
                    ? static async (_, _, ctx, ct) =>
                    {
                        await WaitingRootHandler.Run(ctx, ct);
                        return new JobHandlerOutput(false, null);
                    }
                : static async (_, _, ctx, ct) =>
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
                    MaxOutstanding: maxOutstanding,
                    FullBehavior: fullBehavior
                ),
            ],
            CreateDefaultInput = static () => new NoInput(),
            SerializeInput = null,
            DeadlineSeconds = deadlineSeconds,
        };
}

/// <summary>
/// A PerOccurrence definition's schedule admits each occurrence as a fresh ordinary root charged to the schedule: the
/// root carries the schedule as provenance and a one-contributor envelope, every terminalization releases the charge
/// once, a full schedule waits or drops by its declared behavior, a restart re-charges, a manual trigger is charged,
/// a root that fails for good applies the firing failure policy, and a suspended namespace holds its schedules.
/// </summary>
[ConformanceSpec(
    "schedule.per-occurrence",
    "A PerOccurrence schedule admits roots charged to it and settles them once",
    Area = "Scheduling",
    Contract = "A PerOccurrence schedule admits each occurrence as a root charged to it, releases the charge once per terminalization, and waits or drops when full."
)]
public sealed class PerOccurrenceSchedulingSpec : ActaRuntimeTestBase<ConformanceFixture, PerOccurrenceManifest>
{
    private const int LeaseTtlSeconds = 30;

    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private ExecutionOperations Execution => Services.GetRequiredService<ExecutionOperations>();

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private INamespaces Namespaces => Services.GetRequiredService<IActaOperations>().Namespaces;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
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
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.WaitingJobName, ct);
    }

    [Fact(DisplayName = "A per-occurrence root past its Strict deadline is cancelled at admission and releases its charge")]
    public async Task A_root_past_its_strict_deadline_is_cancelled_at_admission()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.DeadlineJobName, ct);
        await AdmitAtAsync(state.NextOccurrenceAtUtc!.Value, ct);
        var root = Assert.Single(await RootsAsync(schedule.Id, ct));

        // The deadline counts from the root's creation, its admission, against the real clock: an hour back is a root
        // admitted that long ago.
        var anchor = DateTime.UtcNow.AddHours(-1);
        var moved = await Db.From<JobEntity>()
            .Where(j => j.Id == root.Id)
            .UpdateOnlyAsync(() => new JobEntity { CreatedAtUtc = anchor }, ct);
        Assert.Equal(1, moved);

        await Runtime.RunOnceAsync(root.Id, ct);

        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(root.Id, ct)).Status);
        var yielded = (await GetEventsByJobId.Run(Services, root.Id, ct))
            .Where(e => e.EventCode == EventCode.JobExecutionYielded)
            .OrderByDescending(e => e.Id)
            .First();
        Assert.Equal(EventReason.DeadlineExceeded, yielded.Reason);
        Assert.Equal(CheckpointStatusCode.Cancelled, (await EnvelopeAsync(root.Id, ct)).Status);
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.DeadlineJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.DeadlineJobName, ct);
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
        Assert.Equal(ScheduleEvaluationStatusCode.WaitingForCapacity, state.EvaluationStatus);
        Assert.Null(state.NextEvaluationAtUtc);
        Assert.Equal(occurrence.AddMinutes(5), state.NextOccurrenceAtUtc);

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(first.Id, ct));
        (_, state) = await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct);
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, state.EvaluationStatus);
        Assert.NotNull(state.NextEvaluationAtUtc);

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(2, (await RootsAsync(schedule.Id, ct)).Count);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.WaitingJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.WaitingJobName, ct);
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
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, state.EvaluationStatus);
        Assert.Equal(occurrence.AddMinutes(10), state.NextOccurrenceAtUtc);
        Assert.Equal(1, state.OutstandingCount);
        var skipped = Assert.Single(await DefinitionEventsAsync(schedule.DefinitionId, EventCode.ScheduleSkipped, ct));
        Assert.Equal("capacity", EventDetail(skipped, "reason"));
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.DroppingJobName, ct);
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
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.ControlledJobName, ct);
    }

    [Fact(DisplayName = "A manual trigger admits a charged root with an empty envelope, and a full schedule rejects it")]
    public async Task A_manual_trigger_admits_a_charged_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, _) = await ScheduleAsync(PerOccurrenceManifest.TriggeredJobName, ct);
        var lookup = new ScheduleLookup(TestNamespace, PerOccurrenceManifest.TriggeredJobName, PerOccurrenceManifest.ScheduleName);

        Assert.Equal(ControlAction.Applied, (await Schedules.TriggerAsync(lookup, reasonMessage: "now", actor: "ops", ct: ct)).Action);

        var root = Assert.Single(await RootsAsync(schedule.Id, ct));
        Assert.StartsWith("sys.manual.", root.DeduplicationKey, StringComparison.Ordinal);
        var envelope = await EnvelopeAsync(root.Id, ct);
        Assert.Equal(FiringEnvelope.ManualKind, envelope.Firing.Kind);
        Assert.Empty(envelope.Firing.Contributors);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.TriggeredJobName, ct)).State.OutstandingCount);
        var triggered = await ReadSingleEventAsync(root.Id, EventCode.ScheduleTriggered, ct);
        Assert.Equal("now", triggered.EventMessage);
        Assert.Equal(PerOccurrenceManifest.ScheduleName, EventDetail(triggered, "schedule"));

        Assert.Equal(ControlAction.Rejected, (await Schedules.TriggerAsync(lookup, ct: ct)).Action);
        Assert.Single(await RootsAsync(schedule.Id, ct));
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.TriggeredJobName, ct);
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
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.FailingJobName, ct);
    }

    [Fact(
        DisplayName = "Lost attempts that spend the budget settle their roots on reclaim as a completion would, pausing the schedule once"
    )]
    public async Task A_reclaim_that_spends_the_budget_settles_the_roots()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.LostJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        var roots = await RootsAsync(schedule.Id, ct);
        Assert.Equal(2, roots.Count);

        // Each root's only attempt is in flight on a lease that lapsed, so recovery settles both.
        foreach (var root in roots)
        {
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
        }

        var rootIds = roots.Select(root => root.Id).ToList();
        await RecoverySweep.ReclaimUntilAsync(
            Services,
            ns,
            async () =>
                (await Db.From<JobStateEntity>().Where(j => rootIds.Contains(j.Id)).ToListAsync(ct)).All(j =>
                    j.Status != JobStatusCode.Executing
                ),
            ct
        );

        foreach (var root in roots)
        {
            Assert.Equal(JobStatusCode.Failed, (await ReadJobAsync(root.Id, ct)).Status);
            Assert.Equal(CheckpointStatusCode.Failed, (await EnvelopeAsync(root.Id, ct)).Status);
        }
        (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.LostJobName, ct);
        Assert.Equal(0, state.OutstandingCount);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        var paused = await Db.From<EventEntity>()
            .Where(e => e.NamespaceId == ns && e.EventCode == EventCode.SchedulePaused)
            .ToListAsync(ct);
        Assert.Single(paused, e => e.DefinitionId == schedule.DefinitionId);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.LostJobName, ct);
    }

    [Fact(
        DisplayName = "A suspended namespace holds its schedules: nothing is admitted, a trigger is rejected, and a resume hands them back to the admitter"
    )]
    public async Task A_suspended_namespace_holds_its_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.HeldJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        var lookup = new ScheduleLookup(TestNamespace, PerOccurrenceManifest.HeldJobName, PerOccurrenceManifest.ScheduleName);
        Assert.Equal(ControlAction.Applied, (await Namespaces.SuspendAsync(TestNamespace, "hold", ct: ct)).Action);
        try
        {
            Clock.AdvanceTo(occurrence);
            var pass = await Runtime.AdmitOnceAsync(TestNamespace, ct);
            Assert.Equal(0, pass.Admitted);
            Assert.Null(pass.NextWakeUtc);
            Assert.Empty(await RootsAsync(schedule.Id, ct));
            Assert.Equal(ControlAction.Rejected, (await Schedules.TriggerAsync(lookup, ct: ct)).Action);
        }
        finally
        {
            await Namespaces.ResumeAsync(TestNamespace, ct: ct);
        }

        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Single(await RootsAsync(schedule.Id, ct));
        Assert.Equal(occurrence.AddMinutes(5), (await ScheduleAsync(PerOccurrenceManifest.HeldJobName, ct)).State.NextOccurrenceAtUtc);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.HeldJobName, ct);
    }

    [Fact(
        DisplayName = "A batch carrying a stale execution beside the current one of a charged root releases the charge once and records the current outcome"
    )]
    public async Task A_stale_execution_in_a_batch_settles_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, Runtime.RegisteredNamespaceIds[TestNamespace], ct);
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.StaleJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        var root = (await RootsAsync(schedule.Id, ct))[0];

        // The worker still holds the attempt it lost to a re-claim when the attempt that replaced it lands.
        var stale = await ClaimAsync(root.Id, workerId, ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, root.Id, ct);
        var current = await ClaimAsync(root.Id, workerId, ct);

        var rows = (
            await Execution.CompleteExecutionsAsync(
                new CompleteExecutionsCommand(
                    workerId,
                    [CompletionTestOps.Landing(stale, succeeded: false), CompletionTestOps.Landing(current)]
                ),
                ct
            )
        ).Rows;

        Assert.Equal(CompleteExecutionsAction.Completed, Assert.Single(rows, r => r.ExecutionNumber == current.ExecutionNumber).Action);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.StaleJobName, ct)).State.OutstandingCount);
        Assert.Equal(CheckpointStatusCode.Succeeded, (await EnvelopeAsync(root.Id, ct)).Status);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.StaleJobName, ct);
    }

    [Fact(
        DisplayName = "An operator cancel of a running charged root releases the charge once, and the attempt landing after it releases nothing"
    )]
    public async Task A_cancel_of_a_running_root_releases_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, Runtime.RegisteredNamespaceIds[TestNamespace], ct);
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.CancelledJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        var root = (await RootsAsync(schedule.Id, ct))[0];
        var running = await ClaimAsync(root.Id, workerId, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(new JobRef(root.JobRef), ct: ct)).Action);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.CancelledJobName, ct)).State.OutstandingCount);

        Assert.Equal(
            CompleteExecutionsAction.AlreadyTerminal,
            (await Execution.CompleteAsync(workerId, CompletionTestOps.Landing(running), ct)).Action
        );
        Assert.Equal(CheckpointStatusCode.Cancelled, (await EnvelopeAsync(root.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.CancelledJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.CancelledJobName, ct);
    }

    [Fact(
        DisplayName = "A retire releases exactly the roots it cancels, a running root releases when its attempt lands, and a restart after reactivation charges once"
    )]
    public async Task A_retire_releases_the_roots_it_cancels()
    {
        var ct = TestContext.Current.CancellationToken;
        var ns = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, ns, ct);
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.RetiredJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        for (var i = 0; i < 3; i++)
        {
            await AdmitAtAsync(occurrence.AddMinutes(5 * i), ct);
        }
        var roots = await RootsAsync(schedule.Id, ct);
        Assert.Equal(3, roots.Count);
        var (ready, executing, suspended) = (roots[0], roots[1], roots[2]);
        var running = await ClaimAsync(executing.Id, workerId, ct);
        Assert.Equal(RunOnceStatus.Rearmed, await Runtime.RunOnceAsync(suspended.Id, ct));
        Assert.Equal(JobStatusCode.Suspended, (await ReadJobAsync(suspended.Id, ct)).Status);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, PerOccurrenceManifest.RetiredJobName, ct);
        var retired = await Operations.Definitions.RetireAsync(
            TestNamespace,
            PerOccurrenceManifest.RetiredJobName,
            definition!.Version,
            "retired with roots out",
            "ops",
            ct
        );

        Assert.Equal(ControlAction.Applied, retired.Action);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(ready.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Cancelled, (await ReadJobAsync(suspended.Id, ct)).Status);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(executing.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.RetiredJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.RetiredJobName, ct);

        Assert.Equal(
            CompleteExecutionsAction.Completed,
            (await Execution.CompleteAsync(workerId, CompletionTestOps.Landing(running), ct)).Action
        );
        Assert.Equal(CheckpointStatusCode.Succeeded, (await EnvelopeAsync(executing.Id, ct)).Status);
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.RetiredJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.RetiredJobName, ct);

        await DefinitionTestOps.RegisterAsync(
            Services,
            ns,
            [PerOccurrenceManifest.Descriptors.Descriptors.Single(d => d.JobName == PerOccurrenceManifest.RetiredJobName)],
            ct
        );
        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(ready.JobRef), ct: ct)).Action);
        Assert.Equal(CheckpointStatusCode.Pending, (await EnvelopeAsync(ready.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.RetiredJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.RetiredJobName, ct);
    }

    [Fact(DisplayName = "A retire releases nothing for a root whose firing another settlement already settled and released")]
    public async Task A_retire_releases_nothing_for_a_firing_settled_before_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.SettledRetireJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        var root = (await RootsAsync(schedule.Id, ct))[0];
        // What a cancel that commits while the retire runs leaves behind it: the firing settled, its charge released.
        await ForceEnvelopeAsync(PerOccurrenceManifest.SettledRetireJobName, root.Id, CheckpointStatusCode.Cancelled, -1, ct);

        var definition = await Operations.Definitions.GetAsync(TestNamespace, PerOccurrenceManifest.SettledRetireJobName, ct);
        var retired = await Operations.Definitions.RetireAsync(
            TestNamespace,
            PerOccurrenceManifest.SettledRetireJobName,
            definition!.Version,
            "retired after a cancel",
            "ops",
            ct
        );

        Assert.Equal(ControlAction.Applied, retired.Action);
        Assert.Equal(0, (await ScheduleAsync(PerOccurrenceManifest.SettledRetireJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.SettledRetireJobName, ct);
    }

    [Fact(DisplayName = "A cancel releases nothing for a root whose firing was already settled and released")]
    public async Task A_cancel_releases_nothing_for_a_firing_settled_before_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.SettledCancelJobName, ct);
        var occurrence = state.NextOccurrenceAtUtc!.Value;
        await AdmitAtAsync(occurrence, ct);
        await AdmitAtAsync(occurrence.AddMinutes(5), ct);
        var root = (await RootsAsync(schedule.Id, ct))[0];
        await ForceEnvelopeAsync(PerOccurrenceManifest.SettledCancelJobName, root.Id, CheckpointStatusCode.Cancelled, -1, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.CancelAsync(new JobRef(root.JobRef), ct: ct)).Action);

        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.SettledCancelJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.SettledCancelJobName, ct);
    }

    [Fact(DisplayName = "A restart charges nothing for a finished root whose firing is already outstanding")]
    public async Task A_restart_charges_nothing_for_a_firing_already_outstanding()
    {
        var ct = TestContext.Current.CancellationToken;
        var (schedule, state) = await ScheduleAsync(PerOccurrenceManifest.PendingRestartJobName, ct);
        await AdmitAtAsync(state.NextOccurrenceAtUtc!.Value, ct);
        var root = Assert.Single(await RootsAsync(schedule.Id, ct));
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(root.Id, ct));
        await ForceEnvelopeAsync(PerOccurrenceManifest.PendingRestartJobName, root.Id, CheckpointStatusCode.Pending, 1, ct);

        Assert.Equal(ControlAction.Applied, (await Jobs.RestartAsync(new JobRef(root.JobRef), ct: ct)).Action);

        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(root.Id, ct)).Status);
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.PendingRestartJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.PendingRestartJobName, ct);
    }

    [Fact(DisplayName = "An occurrence whose root already exists is passed over without a root or a charge, and the pass admits the rest")]
    public async Task An_occurrence_admitted_before_is_passed_over()
    {
        var ct = TestContext.Current.CancellationToken;
        var (readmitted, state) = await ScheduleAsync(PerOccurrenceManifest.ReadmittedJobName, ct);
        var (beside, _) = await ScheduleAsync(PerOccurrenceManifest.BesideJobName, ct);
        var first = state.NextOccurrenceAtUtc!.Value;
        var second = first.AddMinutes(5);
        await AdmitAtAsync(first, ct);

        // The second occurrence becomes a root of the readmitted schedule, while the other schedule, full, waits for it.
        await AdmitAtAsync(second, ct);
        var admitted = Assert.Single(
            await RootsAsync(readmitted.Id, ct),
            r => r.DeduplicationKey == ScheduleAdmitter.OccurrenceKey(readmitted.Id, second)
        );
        Assert.Equal(
            ScheduleEvaluationStatusCode.WaitingForCapacity,
            (await ScheduleAsync(PerOccurrenceManifest.BesideJobName, ct)).State.EvaluationStatus
        );
        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(Assert.Single(await RootsAsync(beside.Id, ct)).Id, ct));

        // The cursor goes back onto the occurrence it already admitted, as a cursor reseeded behind an admission would.
        Assert.Equal(
            1,
            await Db.From<ScheduleStateEntity>()
                .Where(s => s.Id == readmitted.Id)
                .UpdateOnlyAsync(() => new ScheduleStateEntity { NextOccurrenceAtUtc = second, NextEvaluationAtUtc = second }, ct)
        );

        await Runtime.AdmitOnceAsync(TestNamespace, ct);

        (_, state) = await ScheduleAsync(PerOccurrenceManifest.ReadmittedJobName, ct);
        Assert.Equal(2, (await RootsAsync(readmitted.Id, ct)).Count);
        Assert.Equal(2, state.OutstandingCount);
        Assert.Equal(second.AddMinutes(5), state.NextOccurrenceAtUtc);
        await ReadSingleEventAsync(admitted.Id, EventCode.ScheduleAdmitted, ct);
        Assert.Empty(await DefinitionEventsAsync(readmitted.DefinitionId, EventCode.ScheduleAdmitted, ct));
        Assert.Single(await RootsAsync(beside.Id, ct), r => r.DeduplicationKey == ScheduleAdmitter.OccurrenceKey(beside.Id, second));
        Assert.Equal(1, (await ScheduleAsync(PerOccurrenceManifest.BesideJobName, ct)).State.OutstandingCount);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.ReadmittedJobName, ct);
        await AssertChargeIsPendingEnvelopesAsync(PerOccurrenceManifest.BesideJobName, ct);
    }

    private async Task<ClaimedJob> ClaimAsync(long jobId, int workerId, CancellationToken ct) =>
        Assert.Single(await Execution.ClaimJobAsync(Runtime.RegisteredNamespaceIds[TestNamespace], workerId, LeaseTtlSeconds, jobId, ct));

    /// <summary>
    /// The rule every writer keeps: a schedule's outstanding count is its roots whose firing envelope is Pending, and no
    /// finished root keeps a Pending envelope.
    /// </summary>
    private async Task AssertChargeIsPendingEnvelopesAsync(string jobName, CancellationToken ct)
    {
        var (schedule, state) = await ScheduleAsync(jobName, ct);
        var pending = 0;
        foreach (var root in await RootsAsync(schedule.Id, ct))
        {
            if ((await EnvelopeAsync(root.Id, ct)).Status != CheckpointStatusCode.Pending)
            {
                continue;
            }

            pending++;
            var status = (await ReadJobAsync(root.Id, ct)).Status;
            Assert.False(
                status is JobStatusCode.Succeeded or JobStatusCode.Failed or JobStatusCode.Cancelled,
                $"Root {new JobRef(root.JobRef)} is {status} with its firing envelope still Pending."
            );
        }
        Assert.Equal(pending, state.OutstandingCount);
    }

    /// <summary>Writes a root's envelope status and moves its schedule's count by <paramref name="charge"/>, around the engine.</summary>
    private async Task ForceEnvelopeAsync(string jobName, long rootId, CheckpointStatusCode status, int charge, CancellationToken ct)
    {
        var (schedule, state) = await ScheduleAsync(jobName, ct);
        Assert.Equal(
            1,
            await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == rootId && c.Kind == CheckpointKindCode.Firing)
                .UpdateOnlyAsync(() => new CheckpointEntity { Status = status }, ct)
        );
        var count = state.OutstandingCount + charge;
        Assert.Equal(
            1,
            await Db.From<ScheduleStateEntity>()
                .Where(s => s.Id == schedule.Id)
                .UpdateOnlyAsync(() => new ScheduleStateEntity { OutstandingCount = count }, ct)
        );
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
