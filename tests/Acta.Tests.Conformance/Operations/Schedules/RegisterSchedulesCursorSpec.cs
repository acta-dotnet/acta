using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for the startup schedule-insert path: reading stored cursors, planning them against the declared
/// schedule, and upserting the result. Mirrors <c>CatalogRegistrar.ReconcileSchedulesAsync</c> (GetScheduleState then
/// ScheduleRegistrationPlan.Plan then RegisterSchedules) and asserts the persisted cursor for every (kind x stored-state)
/// cell: a missed occurrence is kept for the admitter to decide under the misfire policy, never decided at a start.
/// </summary>
[ConformanceSpec(
    "schedule.registration-cursor",
    "Schedule insert seeds a new cursor, keeps a stored one, and upserts one row",
    Area = "Scheduling",
    Contract = "The startup schedule insert seeds a new schedule after now, keeps a stored cursor whether due or not, and upserts the single schedule row."
)]
public sealed class RegisterSchedulesCursorSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Base = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const string ScheduleName = "tick";

    private static DateTime At(int minutes) => Base.AddMinutes(minutes);

    [Theory(
        DisplayName = "Insert persists the planned cursor across new, future, and missed cron and interval cells: new seeds after now, a stored cursor is kept under either misfire policy"
    )]
    // New (no stored cursor): first occurrence strictly after now; an interval anchors at now.
    [InlineData("cron-new", ScheduleExpressionKindCode.Cron, Cron5, null, 1, MisfireStrategyCode.Skip, 5)]
    [InlineData("interval-new", ScheduleExpressionKindCode.Interval, "PT5M", null, 1, MisfireStrategyCode.Skip, 6)]
    // Future stored cursor: kept verbatim.
    [InlineData("cron-future", ScheduleExpressionKindCode.Cron, Cron5, 60, 0, MisfireStrategyCode.Skip, 60)]
    [InlineData("interval-future", ScheduleExpressionKindCode.Interval, "PT5M", 60, 0, MisfireStrategyCode.Skip, 60)]
    // Missed stored cursor: kept for the admitter, whichever policy it will apply.
    [InlineData("cron-missed-skip", ScheduleExpressionKindCode.Cron, Cron5, -60, 3, MisfireStrategyCode.Skip, -60)]
    [InlineData("cron-missed-catchup", ScheduleExpressionKindCode.Cron, Cron5, -60, 3, MisfireStrategyCode.CatchUpOnce, -60)]
    [InlineData("interval-missed-skip", ScheduleExpressionKindCode.Interval, "PT5M", 0, 17, MisfireStrategyCode.Skip, 0)]
    [InlineData("interval-missed-catchup", ScheduleExpressionKindCode.Interval, "PT5M", 0, 17, MisfireStrategyCode.CatchUpOnce, 0)]
    public async Task Insert_persists_the_planned_cursor(
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
        var jobName = Unique(label);
        var defId = await CreateDefinitionAsync(jobName, ct);

        // Seed a stored cursor for the future/missed cells; new cells start with no schedule row.
        if (seedOffset is { } so)
        {
            await RegisterAsync(defId, jobName, [Slot(expression, kind, misfire, At(so))], ct);
        }

        var slotId = await PlanAndRegisterAsync(defId, jobName, expression, kind, misfire, At(nowOffset), ct);

        var row = await ScheduleRowAsync(slotId, ct);
        Assert.Equal(At(expectedOffset), row.NextOccurrenceAtUtc);
        Assert.Equal(misfire, row.MisfireStrategy);
    }

    [Fact(DisplayName = "Re-registration upserts the single schedule row and its misfire code rather than duplicating it")]
    public async Task Re_registration_upserts_the_single_schedule_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("upsert");
        var defId = await CreateDefinitionAsync(jobName, ct);

        // First insert seeds the cursor; a second worker run declares a new misfire policy and writes again.
        await RegisterAsync(defId, jobName, [Slot(Cron5, ScheduleExpressionKindCode.Cron, MisfireStrategyCode.Skip, At(0))], ct);
        var slotId = await PlanAndRegisterAsync(
            defId,
            jobName,
            Cron5,
            ScheduleExpressionKindCode.Cron,
            MisfireStrategyCode.CatchUpOnce,
            At(3),
            ct
        );

        var row = await ScheduleRowAsync(slotId, ct);
        Assert.Equal(At(0), row.NextOccurrenceAtUtc);
        Assert.Equal(MisfireStrategyCode.CatchUpOnce, row.MisfireStrategy);
        Assert.True(row.Version > 0, "Upsert must bump the schedule row version, not insert a fresh row.");
    }

    /// <summary>An interval seeds with its anchor, as its first registration stores it, so a later plan keeps its timing.</summary>
    private static SlotSchedule Slot(string expression, ScheduleExpressionKindCode kind, MisfireStrategyCode misfire, DateTime cursor) =>
        new(ScheduleName, expression, null, misfire, kind, null, cursor, kind == ScheduleExpressionKindCode.Interval ? cursor : null);

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    // The startup insert path: read stored cursors, plan the declared schedule at nowUtc, and upsert. This is
    // exactly CatalogRegistrar.ReconcileSchedulesAsync.
    private async Task<long> PlanAndRegisterAsync(
        int defId,
        string jobName,
        string expression,
        ScheduleExpressionKindCode kind,
        MisfireStrategyCode misfire,
        DateTime nowUtc,
        CancellationToken ct
    )
    {
        var stored = (
            await Services.GetRequiredService<GetScheduleStateHandler>().HandleAsync(new GetScheduleStateQuery(TestNamespaceId), ct)
        ).States;
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);

        var declared = new[] { new ScheduleDescriptor(jobName, ScheduleName, expression, null, misfire, kind, null, []) };
        return await RegisterAsync(
            defId,
            jobName,
            ScheduleRegistrationPlan.Plan(
                declared,
                storedForDef,
                nowUtc,
                CalendarRegistry.Empty.Source,
                ScheduleRegistrationPlan.NoCalendars
            ),
            ct
        );
    }

    private async Task<long> RegisterAsync(int defId, string jobName, IReadOnlyList<SlotSchedule> schedules, CancellationToken ct)
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
        var ids = await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
        return ids[defId];
    }

    private async Task<ScheduleRow> ScheduleRowAsync(long slotId, CancellationToken ct) =>
        Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

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
}
