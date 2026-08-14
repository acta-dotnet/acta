using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Operator full-set override of a single named schedule's expression/time zone, CAS-guarded on
/// <c>version</c>. A stale expected version is rejected carrying the schedule's current state; an
/// applied write recomputes the schedule's own cursor under the new effective expression and the owning
/// slot's MIN, and emits an audit-gated <c>schedule.overrides-updated</c> event. Invalid input (a bad
/// expression or an unrecognized time zone) fails in C# before any write.
/// </summary>
[ConformanceSpec(
    "schedule.set-overrides",
    "Operator sets a CAS-guarded full-set schedule expression/time-zone override",
    Area = "Scheduling",
    Contract = "A matching version applies the override and moves the cursor to the new expression, while a stale version is rejected with current state.",
    Arrange = "A recurring slot carries one schedule at its default expression and time zone.",
    Act = "An operator sets, clears, or attempts a stale-version override through ISchedules.UpdateOverridesAsync.",
    Assert = "Applied writes recompute the cursor from the new effective expression and bump version, while rejected or invalid attempts leave the row untouched."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.SetScheduleOverridesAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
public abstract class ScheduleOverridesSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const string DailyCron = "0 0 * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(DisplayName = "Setting an expression override moves the cursor to the new expression's next instant and audits the change")]
    public async Task Setting_an_expression_override_moves_the_cursor_and_audits_the_change()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("expr");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);
        var result = await Schedules.UpdateOverridesAsync(
            Lookup(jobName, "only"),
            before.Version,
            DailyCron,
            null,
            note: "widen",
            actorKey: "operator-1",
            ct: ct
        );
        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(before.Version + 1, result.Version);

        var expectedNext = NextOccurrenceCalculator.Next(DailyCron, null, ScheduleExpressionKindCode.Cron, now);
        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(DailyCron, after.ExpressionOverride);
        Assert.Equal(DailyCron, after.ExpressionEffective);
        Assert.Equal(expectedNext, after.NextRunAtUtc); // moved off the old Cron5 cursor onto the new expression's next instant

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(expectedNext, slot.NextRunAtUtc); // the sole schedule, so the slot MIN follows it

        var list = await Schedules.ListAsync(new ListSchedulesQuery(TestNamespace, jobName), ct);
        var item = Assert.Single(list.Items);
        Assert.Equal(DailyCron, item.Expression); // ListAsync/detail shows the overridden effective expression

        var slotId = await SlotIdAsync(jobName, ct);
        var events = await Db.From<JobEvent>()
            .Where(e => e.JobId == slotId && e.EventCode == EventCode.ScheduleOverridesUpdated)
            .ToListAsync(ct);
        var changed = Assert.Single(events);
        Assert.Equal("operator-1", changed.ActorKey);
        Assert.Contains("only", changed.ReasonMessage);
        Assert.Contains(Cron5, changed.ReasonMessage);
        Assert.Contains(DailyCron, changed.ReasonMessage);
    }

    [Fact(DisplayName = "A stale expected version is rejected with the schedule's current state and nothing changes")]
    public async Task A_stale_expected_version_is_rejected_with_current_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("stale");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);
        var result = await Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), before.Version + 1, DailyCron, null, ct: ct);

        Assert.Equal(ControlAction.Rejected, result.Action);
        Assert.Equal(before.Version, result.Version); // current version, so the caller can re-read

        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.ExpressionOverride);
    }

    [Fact(DisplayName = "Clearing both overrides returns the schedule to its defaults")]
    public async Task Clearing_both_overrides_returns_to_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("clear");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var v1 = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal("UTC", v1.TimeZoneId);
        Assert.Equal("UTC", v1.TimeZoneIdEffective);
        var set = await Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), v1.Version, DailyCron, "Europe/Ljubljana", ct: ct);
        Assert.Equal(ControlAction.Applied, set.Action);

        var v2 = await ScheduleAsync(Db, jobName, "only", ct);
        var cleared = await Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), v2.Version, null, null, ct: ct);
        Assert.Equal(ControlAction.Applied, cleared.Action);

        var v3 = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Null(v3.ExpressionOverride);
        Assert.Null(v3.TimeZoneIdOverride);
        Assert.Equal(v3.Expression, v3.ExpressionEffective); // effective falls back to the definition default
        Assert.Equal(v3.TimeZoneId, v3.TimeZoneIdEffective);
    }

    [Fact(DisplayName = "An invalid expression is rejected in C# before any write")]
    public async Task An_invalid_expression_is_rejected_before_any_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("badexpr");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), before.Version, "not a cron expression", null, ct: ct).AsTask()
        );

        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.ExpressionOverride);
    }

    [Fact(DisplayName = "An unrecognized time zone is rejected in C# before any write")]
    public async Task An_unrecognized_time_zone_is_rejected_before_any_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("badtz");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var before = await ScheduleAsync(Db, jobName, "only", ct);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), before.Version, null, "Not/A_Zone", ct: ct).AsTask()
        );

        var after = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(before.Version, after.Version);
        Assert.Null(after.TimeZoneIdOverride);
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

        Assert.Equal(
            ControlAction.NotFound,
            (await Schedules.UpdateOverridesAsync(Lookup(jobName, "nope"), 0, DailyCron, null, ct: ct)).Action
        );

        // Re-register with no declared schedules: the orphan sweep stamps orphaned_at_utc on "gone".
        await RegisterAsync(db, dialect, defId, jobName, null, [], JobStatusCode.Paused, ct);
        Assert.Equal(
            ControlAction.NotFound,
            (await Schedules.UpdateOverridesAsync(Lookup(jobName, "gone"), 0, DailyCron, null, ct: ct)).Action
        );
    }

    [Fact(DisplayName = "Overriding a paused schedule updates its cursor without waking the slot, and resume honors the new expression")]
    public async Task Overriding_a_paused_schedule_updates_its_cursor_without_waking_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("paused");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        await RegisterAsync(db, dialect, defId, jobName, now.AddMinutes(5), [Slot("only", now.AddMinutes(5))], JobStatusCode.Ready, ct);

        var paused = await Schedules.PauseAsync(Lookup(jobName, "only"), untilUtc: null, ct: ct);
        Assert.Equal(ControlAction.Applied, paused.Action);

        var beforeOverride = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(ScheduleStatusCode.Paused, beforeOverride.Status);

        var result = await Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), beforeOverride.Version, DailyCron, null, ct: ct);
        Assert.Equal(ControlAction.Applied, result.Action);

        var expectedNext = NextOccurrenceCalculator.Next(DailyCron, null, ScheduleExpressionKindCode.Cron, now);
        var afterOverride = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(DailyCron, afterOverride.ExpressionOverride);
        Assert.Equal(DailyCron, afterOverride.ExpressionEffective);
        Assert.Equal(expectedNext, afterOverride.NextRunAtUtc); // a fresh cursor under the new expression, even while paused
        Assert.Equal(ScheduleStatusCode.Paused, afterOverride.Status); // the override does not resurrect the schedule
        Assert.Null(afterOverride.PausedUntilUtc); // still the same indefinite pause

        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Paused, slot.Status); // an indefinite pause still excludes the schedule from the slot MIN
        Assert.Null(slot.NextRunAtUtc);

        var resumed = await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, resumed.Action);
        Assert.Equal(expectedNext, resumed.NextRunAtUtc); // resume reconciles the still-future overridden cursor unchanged

        var resumedSlot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Ready, resumedSlot.Status);
        Assert.Equal(expectedNext, resumedSlot.NextRunAtUtc); // the sole schedule, now honoring the overridden expression
    }

    // ---------- helpers ----------

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private ScheduleLookup Lookup(string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), scheduleName);

    private static SlotSchedule Slot(string name, DateTime? cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

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
