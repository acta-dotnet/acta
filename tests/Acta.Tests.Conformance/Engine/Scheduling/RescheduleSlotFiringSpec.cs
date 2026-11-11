using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Succeeds at once; a slot with nothing to do.</summary>
internal static class SlotHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>One Permanent definition with a five-minute schedule, parked far from any occurrence by the clock.</summary>
public sealed class RescheduleSlotManifest : IJobManifest
{
    public const string JobName = "slot-run-now";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: JobName,
                HandlerType: typeof(SlotHandler),
                MethodName: nameof(SlotHandler.Run),
                InputType: typeof(NoInput),
                ResultType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                ResultPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 3,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.None,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await SlotHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeResult: null
            )
            {
                Schedules =
                [
                    new ScheduleDescriptor(
                        JobName: JobName,
                        ScheduleName: ScheduleName,
                        Expression: "*/5 * * * *",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Cron,
                        Description: null,
                        Environments: []
                    ),
                ],
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
            },
        ]);
}

/// <summary>
/// The job page's "run now" on a permanent slot: a reschedule of an Idle slot, or of a Paused one with no firing
/// outstanding, is a manual firing at the instant (now when none is given), the same envelope a schedule trigger
/// writes, and the slot returns to Idle when it settles; a slot mid-firing keeps its firing and moves its instant. A
/// manual firing is an admission: a suspended namespace takes none, and the slot holds the schedule lock shared for it.
/// </summary>
[ConformanceSpec(
    "job.reschedule-slot",
    "A reschedule of an idle permanent slot is a manual firing at the instant",
    Area = "Scheduling",
    Contract = "RescheduleAsync fires an Idle slot, or a Paused one with no firing, at the instant unless its namespace is suspended, and a slot mid-firing keeps its firing."
)]
public sealed class RescheduleSlotFiringSpec : ActaRuntimeTestBase<ConformanceFixture, RescheduleSlotManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A reschedule with no instant fires an Idle slot now with a manual envelope, and the firing settles the slot Idle")]
    public async Task An_idle_slot_rescheduled_to_now_takes_a_manual_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), reasonMessage: "run now", actor: "ops", ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.NotNull(slot.NextRunAtUtc);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(FiringEnvelope.ManualKind, firing.Kind);
        Assert.Empty(firing.Contributors);
        Assert.Equal("ops", EventDetail(await ReadSingleEventAsync(slotId, EventCode.JobRescheduled, ct), "actor"));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Succeeded, (await EnvelopeAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "A Paused slot with no firing outstanding rescheduled to an instant fires at it with a manual envelope")]
    public async Task A_paused_slot_without_a_firing_takes_a_manual_firing_at_the_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(slotId, ct)).Status);
        var at = (await Clock.GetUtcNowAsync(ct)).AddMinutes(1);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), at, ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(at, slot.NextRunAtUtc);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(FiringEnvelope.ManualKind, firing.Kind);
    }

    [Fact(DisplayName = "A slot paused mid-firing keeps its firing when rescheduled: only the instant moves")]
    public async Task A_slot_mid_firing_keeps_its_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        var occurrence = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(occurrence);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        var admitted = (await EnvelopeAsync(slotId, ct)).Firing;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(admitted.FiringId, firing.FiringId);
        Assert.Equal(FiringEnvelope.ScheduledKind, firing.Kind);
    }

    [Fact(DisplayName = "A reschedule that would fire an Idle or a Paused slot in a suspended namespace is rejected and admits nothing")]
    public async Task A_suspended_namespace_takes_no_manual_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(ControlAction.Applied, (await Operations.Namespaces.SuspendAsync(TestNamespace, ct: ct)).Action);

        await AssertRejectedUnchangedAsync(slotId, JobStatusCode.Idle, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);
        await AssertRejectedUnchangedAsync(slotId, JobStatusCode.Paused, ct);

        // The suspension is the reason: resumed, the same reschedule fires.
        Assert.Equal(ControlAction.Applied, (await Operations.Namespaces.ResumeAsync(TestNamespace, ct: ct)).Action);
        Assert.Equal(ControlAction.Applied, (await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(FiringEnvelope.ManualKind, (await EnvelopeAsync(slotId, ct)).Firing.Kind);
    }

    [Fact(DisplayName = "A slot whose firing was admitted before a suspension still moves its instant when rescheduled")]
    public async Task A_suspended_namespace_still_moves_an_admitted_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        var occurrence = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(occurrence);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        var admitted = (await EnvelopeAsync(slotId, ct)).Firing;
        Assert.Equal(ControlAction.Applied, (await Operations.Namespaces.SuspendAsync(TestNamespace, ct: ct)).Action);
        var at = occurrence.AddMinutes(1);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), at, ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(at, (await ReadJobAsync(slotId, ct)).NextRunAtUtc);
        Assert.Equal(admitted.FiringId, (await EnvelopeAsync(slotId, ct)).Firing.FiringId);
    }

    [Fact(DisplayName = "A reschedule of an Idle slot waits for a suspension in flight, then is rejected")]
    public async Task A_manual_firing_behind_a_suspension_is_rejected()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);

        // A suspension mid-flight: the lock taken exclusive, then the namespace row written, as SuspendNamespace does.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var suspension = await connection.BeginTransactionAsync(ct);
        await ScheduleLockGate.HoldAsync(Db, connection, suspension, TestNamespaceId, exclusive: true, ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = suspension;
            cmd.CommandText =
                $"UPDATE {Db.Schema}.namespaces SET namespace_status_code = 20 /* NamespaceStatusCode.Suspended */ WHERE namespace_id = {TestNamespaceId}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        var pending = Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct).AsTask();
        await LockWaitProbe.WaitAsync(Db, RescheduleSql, LockWait.ApplicationLock, pending, ct);
        await suspension.CommitAsync(ct);

        Assert.Equal(ControlAction.Rejected, (await pending).Action);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Null(await FiringAsync(slotId, ct));
    }

    [Fact(DisplayName = "A suspension waits for a reschedule of an Idle slot in flight, and the manual firing it waited for stands")]
    public async Task A_suspension_waits_for_a_manual_firing_in_flight()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);

        // Holding the slot's row stops the reschedule after its schedule lock is granted and before it writes.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var blocker = await connection.BeginTransactionAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = blocker;
            cmd.CommandText =
                Db.Provider == DbProvider.SqlServer
                    ? $"SELECT job_id FROM {Db.Schema}.jobs_state WITH (UPDLOCK, ROWLOCK) WHERE job_id = {slotId}"
                    : $"SELECT job_id FROM {Db.Schema}.jobs_state WHERE job_id = {slotId} FOR UPDATE";
            Assert.NotNull(await cmd.ExecuteScalarAsync(ct));
        }

        var reschedule = Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct).AsTask();
        await LockWaitProbe.WaitAsync(Db, RescheduleSql, LockWait.Row, reschedule, ct);
        var suspend = Operations.Namespaces.SuspendAsync(TestNamespace, ct: ct).AsTask();
        await LockWaitProbe.WaitAsync(Db, SuspendSql, LockWait.ApplicationLock, suspend, ct);
        await blocker.RollbackAsync(ct);

        Assert.Equal(ControlAction.Applied, (await reschedule).Action);
        Assert.Equal(ControlAction.Applied, (await suspend).Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(FiringEnvelope.ManualKind, (await EnvelopeAsync(slotId, ct)).Firing.Kind);
    }

    /// <summary>Text only the reschedule's statements carry, for <see cref="LockWaitProbe"/>.</summary>
    private string RescheduleSql => Db.Provider == DbProvider.SqlServer ? "@fires" : "_reschedule_job";

    /// <summary>Text only the suspension's statements carry, for <see cref="LockWaitProbe"/>.</summary>
    private string SuspendSql => Db.Provider == DbProvider.SqlServer ? "@p_namespace_name" : "_suspend_namespace_lock";

    /// <summary>A reschedule of the slot is rejected and changes nothing: no state, instant, version, firing, or event.</summary>
    private async Task AssertRejectedUnchangedAsync(long slotId, JobStatusCode status, CancellationToken ct)
    {
        var before = await ReadJobAsync(slotId, ct);
        var events = await CountEventsAsync(slotId, EventCode.JobRescheduled, ct);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct);

        Assert.Equal((ControlAction.Rejected, status, before.Version), (result.Action, result.Status!.Value, result.Version!.Value));
        var after = await ReadJobAsync(slotId, ct);
        Assert.Equal((status, before.NextRunAtUtc, before.Version), (after.Status, after.NextRunAtUtc, after.Version));
        Assert.Equal(events, await CountEventsAsync(slotId, EventCode.JobRescheduled, ct));
        Assert.Null(await FiringAsync(slotId, ct));
    }

    /// <summary>SQLite runs one writer at a time, so no transaction holds a lock beside another.</summary>
    private void SkipOnSqlite() =>
        Assert.SkipWhen(Db.Provider == DbProvider.Sqlite, "SQLite runs one writer at a time, so no second transaction can hold the lock.");

    private Task<CheckpointEntity?> FiringAsync(long jobId, CancellationToken ct) =>
        Db.From<CheckpointEntity>().Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing).SingleOrDefaultAsync(ct);

    private async Task<(CheckpointStatusCode? Status, FiringEnvelope Firing)> EnvelopeAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await FiringAsync(jobId, ct);
        Assert.NotNull(checkpoint);
        var firing = FiringEnvelope.FromJson(checkpoint.Value);
        Assert.NotNull(firing);
        return (checkpoint.Status, firing);
    }
}
