using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Operator pause/resume of a single named schedule as a schedule-level control. Pause leaves the
/// schedule's own cursor untouched and recomputes the owning slot's next run as the MIN over its
/// still-firing schedules (a timed pause contributes only its expiry); resume reconciles the cursor by
/// the misfire policy. Operator state survives a catalog redeploy; orphaned schedules are not
/// controllable; pause/resume emit audit events against the slot job.
/// </summary>
[ConformanceSpec(
    "schedule.pause-resume",
    "Operator pause and resume control a schedule and recompute the owning slot",
    Area = "Scheduling",
    Contract = "Pausing a schedule excludes it from the slot MIN without moving its cursor, resume reconciles by misfire and operator pause survives redeploy.",
    Arrange = "Recurring slots carry single and multi-schedule, timed, missed, orphaned, and redeployed schedule rows.",
    Act = "An operator pauses and resumes named schedules through ISchedules across each case.",
    Assert = "Pause excludes the schedule from the slot MIN without moving its cursor, resume reconciles by misfire, and operator state survives redeploy."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.PauseScheduleAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.ResumeScheduleAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
public abstract class SchedulePauseResumeSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(DisplayName = "Pause keeps the schedule's cursor and sets the slot MIN to the remaining firing schedules")]
    public async Task Pausing_one_of_several_schedules_keeps_its_cursor_and_sets_slot_min_to_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("min");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);

        var soon = now.AddMinutes(10);
        var late = now.AddMinutes(20);
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            now.AddMinutes(10),
            [Slot("soon", soon), Slot("late", late)],
            JobStatusCode.Ready,
            ct
        );

        var result = await Schedules.PauseAsync(Lookup(jobName, "late"), untilUtc: null, reasonMessage: "drain", ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(ScheduleStatusCode.Paused, result.Status);

        var paused = await ScheduleAsync(Db, jobName, "late", ct);
        Assert.Equal(ScheduleStatusCode.Paused, paused.Status);
        Assert.Null(paused.PausedUntilUtc);
        Assert.Equal(late, paused.NextRunAtUtc); // a pause never moves the schedule's own cursor

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(soon, slot.NextRunAtUtc); // MIN over the remaining firing schedule
    }

    [Fact(DisplayName = "Pausing the only schedule system-pauses the slot job and resume re-arms it")]
    public async Task Pausing_the_only_schedule_system_pauses_the_slot_and_resume_re_arms_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("solo");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var paused = await Schedules.PauseAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, paused.Action);

        {
            var slot = await SlotAsync(jobName, ct);
            Assert.Equal(JobStatusCode.Paused, slot.Status);
            Assert.Null(slot.NextRunAtUtc);
        }

        var resumed = await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, resumed.Action);
        Assert.Equal(ScheduleStatusCode.Active, resumed.Status);

        {
            var schedule = await ScheduleAsync(Db, jobName, "only", ct);
            Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
            Assert.Null(schedule.PausedUntilUtc);

            var slot = await SlotAsync(jobName, ct);
            Assert.Equal(JobStatusCode.Ready, slot.Status);
            Assert.NotNull(slot.NextRunAtUtc);
        }
    }

    [Fact(DisplayName = "A timed pause sets the slot wake point to the pause expiry")]
    public async Task Timed_pause_sets_the_slot_wake_point_to_the_expiry()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("timed");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(30), [Slot("only", now.AddMinutes(30))], JobStatusCode.Ready, ct);

        var until = now.AddMinutes(10);
        var result = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: until, ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(until, result.PausedUntilUtc);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal(until, schedule.PausedUntilUtc);

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(until, slot.NextRunAtUtc); // the slot wakes at the pause expiry to auto-resume
    }

    [Fact(DisplayName = "A timed pause with an expiry in the past is rejected")]
    public async Task A_timed_pause_in_the_past_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("past");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var result = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: now.AddMinutes(-1), ct: ct);
        Assert.Equal(ControlAction.Rejected, result.Action);
    }

    [Fact(DisplayName = "Resume reconciles the cursor by misfire: Skip advances past now and FireOnceCatchUp keeps the past instant")]
    public async Task Resume_with_skip_advances_past_now_and_fire_once_catch_up_keeps_the_past_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));

        // Skip: a missed cursor reconciles forward to the next occurrence after now.
        var skipJob = TestKey("skip");
        var skipDef = await CreateDefinitionAsync(db, dialect, skipJob, ct);
        await RegisterAsync(
            db,
            dialect,
            skipDef,
            skipJob,
            now.AddMinutes(-37),
            [Slot("only", now.AddMinutes(-37), MisfireStrategyCode.Skip)],
            JobStatusCode.Ready,
            ct
        );
        await Schedules.PauseAsync(Lookup(skipJob, "only"), ct: ct);
        var skipResumed = await Schedules.ResumeAsync(Lookup(skipJob, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, skipResumed.Action);
        Assert.NotNull(skipResumed.NextRunAtUtc);
        Assert.True(skipResumed.NextRunAtUtc > now, "Skip resume advances strictly past now");

        // FireOnceCatchUp: the past instant is kept so the slot fires once on resume.
        var catchJob = TestKey("catch");
        var catchDef = await CreateDefinitionAsync(db, dialect, catchJob, ct);
        var past = now.AddMinutes(-35); // a real */5 occurrence relative to a 5-minute aligned base is not required here
        await RegisterAsync(
            db,
            dialect,
            catchDef,
            catchJob,
            past,
            [Slot("only", past, MisfireStrategyCode.FireOnceCatchUp)],
            JobStatusCode.Ready,
            ct
        );
        await Schedules.PauseAsync(Lookup(catchJob, "only"), ct: ct);
        var catchResumed = await Schedules.ResumeAsync(Lookup(catchJob, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, catchResumed.Action);
        Assert.NotNull(catchResumed.NextRunAtUtc);
        Assert.True(catchResumed.NextRunAtUtc <= now, "FireOnceCatchUp resume keeps a past instant for one catch-up fire");
    }

    [Fact(DisplayName = "An orphaned schedule cannot be paused or resumed")]
    public async Task An_orphaned_schedule_cannot_be_paused_or_resumed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("orphan");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("gone", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        // Re-register with no declared schedules: the orphan sweep stamps orphaned_at_utc on "gone".
        await RegisterAsync(db, dialect, defId, jobName, null, [], JobStatusCode.Paused, ct);

        Assert.Equal(ControlAction.NotFound, (await Schedules.PauseAsync(Lookup(jobName, "gone"), ct: ct)).Action);
        Assert.Equal(ControlAction.NotFound, (await Schedules.ResumeAsync(Lookup(jobName, "gone"), ct: ct)).Action);
    }

    [Fact(DisplayName = "Catalog re-registration preserves operator pause state")]
    public async Task Catalog_re_registration_does_not_wipe_operator_pause_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("redeploy");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(30), [Slot("only", now.AddMinutes(30))], JobStatusCode.Ready, ct);

        var until = now.AddMinutes(45);
        await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: until, ct: ct);

        // Re-run the startup catalog reconcile for the same definition (GetScheduleState -> Reconcile ->
        // RegisterScheduledJobs), exactly as WorkerRuntimeInitializer.ReconcileSchedulesAsync does.
        await ReReconcileAsync(db, dialect, defId, jobName, now, ct);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, schedule.Status);
        Assert.Equal(until, schedule.PausedUntilUtc);
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
        var jobName = TestKey("orphan-paused");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(30), [Slot("only", now.AddMinutes(30))], JobStatusCode.Ready, ct);

        await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: now.AddMinutes(45), ct: ct);

        // Re-register with the declaration gone: reconciliation orphans the surviving row.
        await RegisterAsync(db, dialect, defId, jobName, null, [], JobStatusCode.Paused, ct);

        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, schedule.Status);
        Assert.Null(schedule.PausedUntilUtc);
    }

    [Fact(
        DisplayName = "Initial sync stores the attribute description with Note left NULL, and catalog re-sync does not overwrite an operator note"
    )]
    public async Task Sync_writes_description_and_resync_preserves_an_operator_note()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("desc");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        const string description = "Nightly cleanup of stale rows.";
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            now.AddMinutes(5),
            [Slot("only", now.AddMinutes(5), description: description)],
            JobStatusCode.Ready,
            ct
        );

        var seeded = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(description, seeded.Description);
        Assert.Null(seeded.Note);

        await Schedules.PauseAsync(Lookup(jobName, "only"), reasonMessage: "operator drain", ct: ct);

        // Re-run the startup catalog reconcile carrying the same declared description, exactly as
        // WorkerRuntimeInitializer.ReconcileSchedulesAsync does on every worker start.
        await ReReconcileAsync(db, dialect, defId, jobName, now, ct, description);

        var resynced = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(description, resynced.Description); // description keeps tracking the attribute
        Assert.Equal("operator drain", resynced.Note); // the operator note survives the re-sync
    }

    [Fact(DisplayName = "Pause and resume emit audit events against the slot job")]
    public async Task Pause_and_resume_emit_audit_events_against_the_slot_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("audit");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        await Schedules.PauseAsync(Lookup(jobName, "only"), reasonMessage: "maintenance", ct: ct);
        await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);

        var slotId = await SlotIdAsync(jobName, ct);
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var paused = events.SingleOrDefault(e => e.EventCode == EventCode.SchedulePaused);
        var resumed = events.SingleOrDefault(e => e.EventCode == EventCode.ScheduleResumed);
        Assert.NotNull(paused);
        Assert.NotNull(resumed);
        Assert.Equal("only", paused!.ReasonMessage); // the schedule name rides reason_message
        Assert.Equal("only", resumed!.ReasonMessage);
    }

    // ---------- helpers ----------

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private ScheduleLookup Lookup(string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), scheduleName);

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
        var stored = await Services.GetRequiredService<IScheduleStore>().GetScheduleStateAsync(TestNamespaceId, ct);
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
        var (slotSchedules, slotMin) = ScheduleWalker.Reconcile(declared, storedForDef, now);
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            slotMin,
            slotSchedules,
            slotMin is null ? JobStatusCode.Paused : JobStatusCode.Ready,
            ct
        );
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = await Jobs().GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct);
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
