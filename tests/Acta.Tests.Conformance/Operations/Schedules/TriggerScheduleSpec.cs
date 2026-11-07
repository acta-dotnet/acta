using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Operator manual trigger of a single named schedule: admits a manual firing of the owning slot, a pending envelope
/// with no contributors, so the next claim runs it, or a new root on a per-occurrence schedule, leaving the schedule's
/// own cursor (and cadence) untouched and answering the ref of the job it made ready. A paused schedule, a slot that is
/// not Idle, or a per-occurrence schedule at capacity rejects rather than queueing a second firing.
/// </summary>
[ConformanceSpec(
    "schedule.trigger",
    "Operator manually fires a schedule now without disturbing its cadence",
    Area = "Scheduling",
    Contract = "A trigger makes the Idle slot or a new per-occurrence root ready, answers its ref, and keeps the cursor, and a paused, busy, or full schedule rejects."
)]
public sealed class TriggerScheduleSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(
        DisplayName = "Triggering an Idle slot's schedule admits a manual firing now, leaves the schedule's own cursor untouched, audits it, and rejects a second one behind it"
    )]
    public async Task Triggering_an_idle_slot_admits_a_manual_firing_and_audits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("fire");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", farFuture)], ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);

        var tBefore = await NowAsync(ct);
        var result = await Schedules.TriggerAsync(Lookup(jobName, "only"), reasonMessage: "fire now", ct: ct);
        var tAfter = await NowAsync(ct);

        Assert.Equal(ControlAction.Applied, result.Action);

        // The slot is claimable at once, carrying the manual firing's envelope for the attempt to read, and the answer
        // names it as the job the call fired.
        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(new JobRef(slot.JobRef), result.JobRef);
        Assert.NotNull(slot.NextRunAtUtc);
        Assert.InRange(slot.NextRunAtUtc!.Value, tBefore.AddSeconds(-2), tAfter.AddSeconds(10));
        var slotId = await SlotIdAsync(jobName, ct);
        var firing = await FiringAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, firing.Status);
        var envelope = FiringEnvelope.FromJson(firing.Value);
        Assert.NotNull(envelope);
        Assert.Equal(FiringEnvelope.ManualKind, envelope.Kind);
        Assert.Empty(envelope.Contributors);

        // A second trigger finds the slot busy with the first firing and rejects rather than queueing behind it.
        var second = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Rejected, second.Action);
        Assert.Null(second.JobRef);

        // The schedule's OWN cursor and version are never written by this verb: cadence is preserved.
        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(farFuture, after.NextOccurrenceAtUtc);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ScheduleStatusCode.Active, after.Status);

        var triggered = await ReadSingleEventAsync(slotId, EventCode.ScheduleTriggered, ct);
        Assert.Equal("only", EventDetail(triggered, "schedule"));
        Assert.Equal("fire now", triggered.EventMessage);
    }

    [Fact(
        DisplayName = "Triggering a per-occurrence schedule admits a new root ready at once, answers its ref, and rejects a second one at capacity"
    )]
    public async Task Triggering_a_per_occurrence_schedule_answers_the_root_it_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("roots");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct, ScheduledDeliveryCode.PerOccurrence);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", farFuture)], ct, ScheduledDeliveryCode.PerOccurrence);
        var schedule = await Db.From<ScheduleEntity>()
            .Where(s => s.DefinitionId == defId && s.ScheduleName == "only")
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(schedule);

        var result = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        var root = Assert.Single(await Db.From<JobEntity>().Where(j => j.ScheduleId == schedule.Id).ToListAsync(ct));
        Assert.Equal(new JobRef(root.JobRef), result.JobRef);
        Assert.Equal(JobKindCode.Ordinary, root.Kind);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(root.Id, ct)).Status);
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == schedule.Id).SingleOrDefaultAsync(ct);
        Assert.Equal(farFuture, state!.NextOccurrenceAtUtc);

        // The root is outstanding against a capacity of one, so a second trigger is rejected and answers no job.
        var second = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Rejected, second.Action);
        Assert.Null(second.JobRef);
    }

    [Fact(DisplayName = "Triggering a paused schedule is rejected and leaves the slot and schedule untouched")]
    public async Task Triggering_a_paused_schedule_is_rejected_and_leaves_state_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("paused");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", farFuture)], ct);

        var paused = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: null, ct: ct);
        Assert.Equal(ControlAction.Applied, paused.Action);

        var beforeSlot = await SlotAsync(jobName, ct);
        var result = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Rejected, result.Action);

        var afterSlot = await SlotAsync(jobName, ct);
        Assert.Equal(beforeSlot.Status, afterSlot.Status);
        Assert.Equal(beforeSlot.NextRunAtUtc, afterSlot.NextRunAtUtc);
        Assert.Equal(beforeSlot.Version, afterSlot.Version);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
    }

    [Fact(DisplayName = "Triggering a schedule whose slot is mid-execution is rejected because a fire is already in flight")]
    public async Task Triggering_a_schedule_mid_execution_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("inflight");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", farFuture)], ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await SetRuntimeStatusAsync(db, slotId, (byte)JobStatusCode.Executing, ct);

        var result = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Rejected, result.Action);

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Executing, slot.Status); // untouched: the in-flight execution owns the row
        Assert.Null(slot.NextRunAtUtc);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(farFuture, schedule.NextOccurrenceAtUtc); // the schedule's own cursor is never touched by this verb
    }

    [Fact(DisplayName = "Triggering a schedule whose slot is terminal is rejected with no phantom applied and no schedule.triggered event")]
    public async Task Triggering_a_terminal_slot_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("terminal");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", farFuture)], ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await SetRuntimeStatusAsync(db, slotId, (byte)JobStatusCode.Succeeded, ct);

        var result = await Schedules.TriggerAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Rejected, result.Action);

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Succeeded, slot.Status); // untouched
        Assert.Null(slot.NextRunAtUtc); // no phantom cursor move
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleTriggered, ct)); // no schedule.triggered
    }

    [Fact(DisplayName = "An unknown or orphaned schedule reports not found")]
    public async Task An_unknown_or_orphaned_schedule_reports_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("orphan");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("gone", now.AddMinutes(5))], ct);

        Assert.Equal(ControlAction.NotFound, (await Schedules.TriggerAsync(Lookup(jobName, "nope"), ct: ct)).Action);

        // Re-register with no declared schedules: the orphan sweep orphans "gone".
        await RegisterAsync(db, dialect, defId, jobName, [], ct);
        Assert.Equal(ControlAction.NotFound, (await Schedules.TriggerAsync(Lookup(jobName, "gone"), ct: ct)).Action);
    }

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private ScheduleLookup Lookup(string jobName, string scheduleName) => new(TestNamespace, jobName, scheduleName);

    private static SlotSchedule Slot(string name, DateTime? cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private async Task<CheckpointEntity> FiringAsync(long slotId, CancellationToken ct) =>
        Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).ToListAsync(ct)
        );

    private static Task SetRuntimeStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        JobStateStaging.SetStatusAsync(db, jobId, statusCode, ct);

    private async Task<int> CreateDefinitionAsync(
        IDbSession db,
        ISqlDialect dialect,
        string jobName,
        CancellationToken ct,
        ScheduledDeliveryCode delivery = ScheduledDeliveryCode.Permanent
    )
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName, delivery)], ct);
        return map[jobName];
    }

    private async Task RegisterAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        IReadOnlyList<SlotSchedule> schedules,
        CancellationToken ct,
        ScheduledDeliveryCode delivery = ScheduledDeliveryCode.Permanent
    )
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: delivery,
            Schedules: schedules
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private Task<long> SlotIdAsync(string jobName, CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, jobName, ct);

    private IJobs Jobs() => Services.GetRequiredService<IJobs>();

    private async Task<ScheduleRow> ScheduleAsync(IDbSession session, string jobName, string scheduleName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        return Assert.Single(await ScheduleTestOps.ReadAsync(session, slotId, ct), s => s.ScheduleName == scheduleName);
    }

    private async Task<TestJobRow> SlotAsync(string jobName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        return await ReadJobAsync(slotId, ct);
    }

    private static JobDescriptor Def(string name, ScheduledDeliveryCode delivery) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            ResultType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            ResultPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: delivery,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );
}
