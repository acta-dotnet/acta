using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Conformance for the startup schedule-insert path: reading stored cursors, reconciling them against
/// the declared schedule under its misfire policy, and upserting the result. Mirrors
/// <c>WorkerRuntimeInitializer.ReconcileSchedulesAsync</c> (GetScheduleState then ScheduleWalker.Reconcile
/// then RegisterScheduledJobs) and asserts the persisted cursor for every (kind x stored-state x misfire)
/// cell, plus that re-registration upserts the single schedule row rather than duplicating it.
/// </summary>
[ConformanceSpec(
    "register-scheduled-jobs.insert-misfire-matrix",
    "Schedule insert reconciles the cursor per misfire policy and upserts one row",
    Area = "Scheduling",
    Contract = "The startup schedule insert persists the misfire-reconciled next-run cursor and upserts the single schedule row.",
    Arrange = "Cron and interval schedules are prepared with new, future, and missed stored cursors under each misfire policy.",
    Act = "The startup reconcile reads the stored state, reconciles each cell, registers the result, then re-registers the same definition.",
    Assert = "Every (kind x stored-state x misfire) cell persists the reconciled next-run cursor and re-registration upserts one row without duplicating."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetScheduleStateAsync))]
public abstract class ScheduleInsertMisfireMatrixSpec<TFixture> : ActaStorageTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Base = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const string ScheduleName = "tick";

    private static DateTime At(int minutes) => Base.AddMinutes(minutes);

    [Theory(
        DisplayName = "Insert persists the misfire-reconciled cursor across new, future, and missed cron and interval cells: new seeds after now, future is kept, Skip advances past now, FireOnceCatchUp keeps the past instant"
    )]
    // New (no stored cursor): first occurrence strictly after now; misfire policy is irrelevant.
    [InlineData("cron-new", ScheduleExpressionKindCode.Cron, Cron5, null, 1, MisfireStrategyCode.Skip, 5)]
    [InlineData("interval-new", ScheduleExpressionKindCode.Interval, "PT5M", null, 1, MisfireStrategyCode.Skip, 6)]
    // Future stored cursor (un-missed): kept verbatim; misfire policy is irrelevant.
    [InlineData("cron-future", ScheduleExpressionKindCode.Cron, Cron5, 60, 0, MisfireStrategyCode.Skip, 60)]
    [InlineData("interval-future", ScheduleExpressionKindCode.Interval, "PT5M", 60, 0, MisfireStrategyCode.Skip, 60)]
    // Missed + Skip: advance to the first occurrence strictly after now.
    [InlineData("cron-missed-skip", ScheduleExpressionKindCode.Cron, Cron5, -60, 3, MisfireStrategyCode.Skip, 5)]
    [InlineData("interval-missed-skip", ScheduleExpressionKindCode.Interval, "PT5M", 0, 17, MisfireStrategyCode.Skip, 20)]
    // Missed + FireOnceCatchUp: keep the past instant for one coalesced catch-up fire.
    [InlineData("cron-missed-catchup", ScheduleExpressionKindCode.Cron, Cron5, -60, 3, MisfireStrategyCode.FireOnceCatchUp, -60)]
    [InlineData("interval-missed-catchup", ScheduleExpressionKindCode.Interval, "PT5M", 0, 17, MisfireStrategyCode.FireOnceCatchUp, 0)]
    public async Task Insert_persists_the_reconciled_cursor(
        string label,
        ScheduleExpressionKindCode kind,
        string expression,
        int? seedOffset,
        int nowOffset,
        MisfireStrategyCode misfire,
        int expectedOffset
    )
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var jobName = TestKey(label);

        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);

        // Seed a stored cursor for the future/missed cells; new cells start with no schedule row.
        if (seedOffset is { } so)
        {
            await RegisterAsync(db, dialect, defId, jobName, [Slot(expression, kind, misfire, At(so))], JobStatusCode.Ready, At(so), ct);
        }

        var slotId = await ReconcileAndRegisterAsync(db, dialect, defId, jobName, expression, kind, misfire, At(nowOffset), ct);

        var row = await ScheduleRowAsync(slotId, ct);
        Assert.Equal(At(expectedOffset), row.NextRunAtUtc);
        Assert.Equal(misfire, row.Misfire);
    }

    [Fact(DisplayName = "Re-registration upserts the single schedule row and its misfire code rather than duplicating it")]
    public async Task Re_registration_upserts_the_single_schedule_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var jobName = TestKey("upsert");

        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);

        // First insert seeds the cursor; a second worker run reconciles and writes again.
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            [Slot(Cron5, ScheduleExpressionKindCode.Cron, MisfireStrategyCode.Skip, At(0))],
            JobStatusCode.Ready,
            At(0),
            ct
        );
        var slotId = await ReconcileAndRegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            Cron5,
            ScheduleExpressionKindCode.Cron,
            MisfireStrategyCode.Skip,
            At(3),
            ct
        );

        var rows = await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var row = Assert.Single(rows);
        Assert.Equal(At(5), row.NextRunAtUtc);
        Assert.True(row.Version > 0, "Upsert must bump the schedule row version, not insert a fresh row.");
    }

    // ---------- helpers ----------

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    private static SlotSchedule Slot(string expression, ScheduleExpressionKindCode kind, MisfireStrategyCode misfire, DateTime? cursor) =>
        new(ScheduleName, expression, null, misfire, kind, null, cursor);

    private async Task<int> CreateDefinitionAsync(IDbSession db, ISqlDialect dialect, string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    // The startup insert path: read stored cursors, reconcile the declared schedule under its misfire
    // policy at nowUtc, and upsert. This is exactly WorkerRuntimeInitializer.ReconcileSchedulesAsync.
    private async Task<long> ReconcileAndRegisterAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        string expression,
        ScheduleExpressionKindCode kind,
        MisfireStrategyCode misfire,
        DateTime nowUtc,
        CancellationToken ct
    )
    {
        var stored = await Services.GetRequiredService<IScheduleStore>().GetScheduleStateAsync(TestNamespaceId, ct);
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);

        var declared = new[] { new ScheduleDescriptor(jobName, ScheduleName, expression, null, misfire, kind, null, []) };
        var (slotSchedules, slotMin) = ScheduleWalker.Reconcile(declared, storedForDef, nowUtc);

        return await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            slotSchedules,
            slotMin is null ? JobStatusCode.Paused : JobStatusCode.Ready,
            slotMin,
            ct
        );
    }

    private async Task<long> RegisterAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus,
        DateTime? slotMin,
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
        var ids = await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
        return ids[defId];
    }

    private async Task<JobSchedule> ScheduleRowAsync(long slotId, CancellationToken ct)
    {
        var rows = await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        return Assert.Single(rows);
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
