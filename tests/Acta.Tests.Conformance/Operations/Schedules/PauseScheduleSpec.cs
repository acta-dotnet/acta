using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>Conformance for operator schedule pause: the schedule leaves the admitter's seek with its cursor kept, a timed pause ends on its own, and the pause survives redeploy.</summary>
[ConformanceSpec(
    "schedule.pause",
    "Operator pause takes a schedule out of admission and survives redeploy",
    Area = "Scheduling",
    Contract = "Pausing a schedule keeps its cursor, leaves its slot alone, is re-evaluated at a timed expiry and not before, and survives catalog re-sync."
)]
public sealed class PauseScheduleSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private ScheduleLookup Lookup(string jobName, string scheduleName) => new(TestNamespace, jobName, scheduleName);

    private static SlotSchedule Slot(
        string name,
        DateTime? cursor,
        MisfireStrategyCode misfire = MisfireStrategyCode.Skip,
        string? description = null
    ) => new(name, Cron5, null, misfire, ScheduleExpressionKindCode.Cron, description, cursor);

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
        IReadOnlyList<SlotSchedule> schedules,
        CancellationToken ct
    )
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules: schedules
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private async Task ReReconcileAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        DateTime now,
        CancellationToken ct,
        string? description = null
    )
    {
        var stored = (
            await Services.GetRequiredService<GetScheduleStateHandler>().HandleAsync(new GetScheduleStateQuery(TestNamespaceId), ct)
        ).States;
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);
        var declared = new[]
        {
            new ScheduleDescriptor(
                jobName,
                "only",
                Cron5,
                null,
                MisfireStrategyCode.Skip,
                ScheduleExpressionKindCode.Cron,
                description,
                []
            ),
        };
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            ScheduleRegistrationPlan.Plan(declared, storedForDef, now, CalendarRegistry.Empty.Source, ScheduleRegistrationPlan.NoCalendars),
            ct
        );
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

    private static JobDescriptor Def(string name) =>
        new(
            JobName: name,
            HandlerType: typeof(object),
            MethodName: "M",
            InputType: typeof(int),
            OutputType: null,
            InputPayloadFormat: JobPayloadFormat.Json,
            OutputPayloadFormat: null,
            Priority: default,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    [Fact(
        DisplayName = "Pause keeps the schedule's cursor, takes it out of the admitter's seek, and leaves its sibling and the slot alone"
    )]
    public async Task Pausing_one_of_several_schedules_keeps_its_cursor_and_leaves_the_rest_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("min");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);

        var soon = now.AddMinutes(10);
        var late = now.AddMinutes(20);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("soon", soon), Slot("late", late)], ct);

        var result = await Schedules.PauseAsync(Lookup(jobName, "late"), untilUtc: null, reasonMessage: "drain", ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(ScheduleStatusCode.Paused, result.Status);

        var paused = await ScheduleAsync(Db, jobName, "late", ct);
        Assert.Equal(ScheduleStatusCode.Paused, paused.Status);
        Assert.Null(paused.PausedUntilUtc);
        Assert.Equal(late, paused.NextOccurrenceAtUtc); // a pause never moves the schedule's own cursor
        Assert.Null(paused.NextEvaluationAtUtc); // nothing timed ends an indefinite pause

        var sibling = await ScheduleAsync(Db, jobName, "soon", ct);
        Assert.Equal(soon, sibling.NextEvaluationAtUtc);
        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "Pausing the only schedule leaves the slot Idle, and a resume returns the schedule to the admitter at once")]
    public async Task Pausing_the_only_schedule_leaves_the_slot_idle_and_resume_returns_it_to_the_admitter()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("solo");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(5))], ct);

        var paused = await Schedules.PauseAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, paused.Action);

        {
            var slot = await SlotAsync(jobName, ct);
            Assert.Equal(JobStatusCode.Idle, slot.Status);
            Assert.Null(slot.NextRunAtUtc);
        }

        var resumed = await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, resumed.Action);
        Assert.Equal(ScheduleStatusCode.Active, resumed.Status);

        {
            var schedule = await ScheduleAsync(Db, jobName, "only", ct);
            Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
            Assert.Null(schedule.PausedUntilUtc);
            Assert.Equal(EvaluationStatusCode.Resumed, schedule.EvaluationStatus);
            Assert.InRange(schedule.NextEvaluationAtUtc!.Value, now, now.AddMinutes(1));

            var slot = await SlotAsync(jobName, ct);
            Assert.Equal(JobStatusCode.Idle, slot.Status);
            Assert.Null(slot.NextRunAtUtc);
        }
    }

    [Fact(DisplayName = "A timed pause sets the schedule's next evaluation to the pause expiry")]
    public async Task Timed_pause_sets_the_next_evaluation_to_the_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("timed");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(30))], ct);

        var until = now.AddMinutes(10);
        var result = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: until, ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(until, result.PausedUntilUtc);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal(until, schedule.PausedUntilUtc);
        Assert.Equal(until, schedule.NextEvaluationAtUtc); // the admitter ends the pause at its expiry

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "A timed pause with an expiry in the past is rejected")]
    public async Task A_timed_pause_in_the_past_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("past");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(5))], ct);

        var result = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: now.AddMinutes(-1), ct: ct);
        Assert.Equal(ControlAction.Rejected, result.Action);
    }

    [Fact(DisplayName = "An orphaned schedule cannot be paused or resumed")]
    public async Task An_orphaned_schedule_cannot_be_paused_or_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("orphan");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("gone", now.AddMinutes(5))], ct);

        // Re-register with no declared schedules: the orphan sweep orphans "gone".
        await RegisterAsync(db, dialect, defId, jobName, [], ct);

        Assert.Equal(ControlAction.NotFound, (await Schedules.PauseAsync(Lookup(jobName, "gone"), ct: ct)).Action);
        Assert.Equal(ControlAction.NotFound, (await Schedules.ResumeAsync(Lookup(jobName, "gone"), ct: ct)).Action);
    }

    [Fact(DisplayName = "Catalog re-registration preserves operator pause state")]
    public async Task Catalog_re_registration_does_not_wipe_operator_pause_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("redeploy");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(30))], ct);

        var until = now.AddMinutes(45);
        await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: until, ct: ct);

        // Re-run the startup catalog reconcile for the same definition (GetScheduleState -> ScheduleRegistrationPlan ->
        // RegisterSchedules), exactly as CatalogRegistrar.ReconcileSchedulesAsync does.
        await ReReconcileAsync(db, dialect, defId, jobName, now, ct);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal(until, schedule.PausedUntilUtc);
        Assert.Equal(until, schedule.NextEvaluationAtUtc);
    }

    [Fact(DisplayName = "Orphaning a timed-paused schedule clears the pause deadline along with the status")]
    public async Task Orphaning_a_paused_schedule_clears_its_pause_deadline()
    {
        // ck_schedules_paused_pair only allows a pause deadline while the row is Paused, so the
        // reconcile that orphans a declaration must clear it in the same write. An orphaned schedule
        // cannot fire, which makes a pending expiry meaningless anyway.
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("orphan-paused");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(30))], ct);

        await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: now.AddMinutes(45), ct: ct);

        // Re-register with the declaration gone: reconciliation orphans the surviving row.
        await RegisterAsync(db, dialect, defId, jobName, [], ct);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, schedule.Status);
        Assert.Null(schedule.PausedUntilUtc);
        Assert.Null(schedule.NextOccurrenceAtUtc);
        Assert.Null(schedule.NextEvaluationAtUtc);
    }

    [Fact(DisplayName = "Pause and resume emit audit events against the slot job")]
    public async Task Pause_and_resume_emit_audit_events_against_the_slot_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("audit");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", now.AddMinutes(5))], ct);

        await Schedules.PauseAsync(Lookup(jobName, "only"), reasonMessage: "maintenance", ct: ct);
        await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);

        var slotId = await SlotIdAsync(jobName, ct);
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var paused = events.SingleOrDefault(e => e.EventCode == EventCode.SchedulePaused);
        var resumed = events.SingleOrDefault(e => e.EventCode == EventCode.ScheduleResumed);
        Assert.NotNull(paused);
        Assert.NotNull(resumed);
        // The schedule name rides the details; the operator's text is the message.
        Assert.Equal("only", paused!.Schedule);
        Assert.Equal("maintenance", paused.Message);
        Assert.Equal("only", resumed!.Schedule);
        Assert.Null(resumed.Message);
    }
}
