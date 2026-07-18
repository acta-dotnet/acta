using Acta.Features.Definitions;
using Acta.Features.Schedules;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Operator manual trigger of a single named schedule: pulls the owning slot's cursor to now so the
/// next claim sweep fires it immediately, leaving the schedule's own cursor (and cadence) untouched. A
/// paused schedule or a slot already in flight (Dispatched/Executing) rejects rather than firing twice.
/// </summary>
[ConformanceSpec(
    "schedule.trigger-now",
    "Operator manually fires a schedule now without disturbing its cadence",
    Area = "Scheduling",
    Contract = "Triggering an eligible schedule makes its slot claimable now without moving the schedule's own cursor, while paused or in-flight schedules reject.",
    Arrange = "A recurring slot carries one schedule at a far-future cursor, optionally paused or mid-execution.",
    Act = "An operator triggers the named schedule through ISchedules.TriggerNowAsync.",
    Assert = "An eligible trigger pulls the slot cursor to now and audits it, while paused, in-flight, or unknown targets reject or report not found untouched."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.TriggerScheduleNowAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
public abstract class ScheduleTriggerNowSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IJobs>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(
        DisplayName = "Triggering a Ready schedule pulls the slot cursor to now, leaves the schedule's own cursor untouched, and audits the fire"
    )]
    public async Task Triggering_a_ready_schedule_pulls_the_slot_cursor_to_now_and_audits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("fire");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, farFuture, [Slot("only", farFuture)], JobStatusCode.Ready, ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);

        var tBefore = await NowAsync(ct);
        var result = await Schedules.TriggerNowAsync(Lookup(jobName, "only"), note: "fire now", ct: ct);
        var tAfter = await NowAsync(ct);

        Assert.Equal(JobControlAction.Applied, result.Action);

        // The slot is now claimable immediately: its cursor moved to (approximately) now and it's Ready.
        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.NotNull(slot.NextRunAtUtc);
        Assert.InRange(slot.NextRunAtUtc!.Value, tBefore.AddSeconds(-2), tAfter.AddSeconds(10));

        // The schedule's OWN cursor and version are never written by this verb: cadence is preserved.
        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(farFuture, after.NextRunAtUtc);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(ScheduleStatusCode.Active, after.Status);

        var slotId = await SlotIdAsync(jobName, ct);
        var events = await Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == JobEventCode.ScheduleTriggered)
            .ToListAsync(ct);
        var triggered = Assert.Single(events);
        Assert.Equal("only: fire now", triggered.ReasonMessage); // the schedule name plus the operator note rides reason_message
    }

    [Fact(DisplayName = "Triggering a paused schedule is rejected and leaves the slot and schedule untouched")]
    public async Task Triggering_a_paused_schedule_is_rejected_and_leaves_state_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("paused");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, farFuture, [Slot("only", farFuture)], JobStatusCode.Ready, ct);

        var paused = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: null, ct: ct);
        Assert.Equal(JobControlAction.Applied, paused.Action);

        var beforeSlot = await SlotAsync(jobName, ct);
        var result = await Schedules.TriggerNowAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(JobControlAction.Rejected, result.Action);

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
        var jobName = TestKey("inflight");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, farFuture, [Slot("only", farFuture)], JobStatusCode.Ready, ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await SetRuntimeStatusAsync(db, slotId, (byte)JobStatusCode.Executing, ct);

        var result = await Schedules.TriggerNowAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(JobControlAction.Rejected, result.Action);

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Executing, slot.Status); // untouched: the in-flight execution owns the row
        Assert.Equal(farFuture, slot.NextRunAtUtc);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(farFuture, schedule.NextRunAtUtc); // the schedule's own cursor is never touched by this verb
    }

    [Fact(DisplayName = "Triggering a schedule whose slot is terminal is rejected with no phantom applied and no schedule.triggered event")]
    public async Task Triggering_a_terminal_slot_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("terminal");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var farFuture = now.AddHours(6);
        await RegisterAsync(db, dialect, defId, jobName, farFuture, [Slot("only", farFuture)], JobStatusCode.Ready, ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await SetRuntimeStatusAsync(db, slotId, (byte)JobStatusCode.Done, ct);

        var result = await Schedules.TriggerNowAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(JobControlAction.Rejected, result.Action);

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Done, slot.Status); // untouched
        Assert.Equal(farFuture, slot.NextRunAtUtc); // no phantom cursor move
        var events = await Db.From<Acta.Relational.Entities.JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == JobEventCode.ScheduleTriggered)
            .ToListAsync(ct);
        Assert.Empty(events); // no schedule.triggered
    }

    [Fact(DisplayName = "An unknown or orphaned schedule reports not found")]
    public async Task An_unknown_or_orphaned_schedule_reports_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("orphan");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("gone", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        Assert.Equal(JobControlAction.NotFound, (await Schedules.TriggerNowAsync(Lookup(jobName, "nope"), ct: ct)).Action);

        // Re-register with no declared schedules: the orphan sweep stamps orphaned_at_utc on "gone".
        await RegisterAsync(db, dialect, defId, jobName, null, [], JobStatusCode.Paused, ct);
        Assert.Equal(JobControlAction.NotFound, (await Schedules.TriggerNowAsync(Lookup(jobName, "gone"), ct: ct)).Action);
    }

    // ---------- helpers ----------

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private JobScheduleLookup Lookup(string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), scheduleName);

    private static SlotSchedule Slot(string name, DateTime? cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private static Task SetRuntimeStatusAsync(IDbSession db, long jobId, byte statusCode, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status WHERE job_id = @p_id",
            ct,
            ("@p_status", statusCode),
            ("@p_id", jobId)
        );

    private async Task<int> CreateDefinitionAsync(IDbSession db, ISqlDialect dialect, string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private async Task RegisterAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        DateTime? slotMin,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus,
        CancellationToken ct
    )
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            JobName: jobName,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            AuditLevel: JobAuditLevelCode.Audit,
            SlotStatus: slotStatus,
            SlotMinNextRunAtUtc: slotMin,
            Schedules: schedules
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = await Jobs().ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private IJobs Jobs() => Services.GetRequiredService<IJobs>();

    private async Task<JobSchedule> ScheduleAsync(IDbSession session, string jobName, string scheduleName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        var rows = await session.From<JobSchedule>().Where(s => s.JobId == slotId && s.Name == scheduleName).ToListAsync(ct);
        return Assert.Single(rows);
    }

    private async Task<TestJobRow> SlotAsync(string jobName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        return await ReadJobAsync(slotId, ct);
    }

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
