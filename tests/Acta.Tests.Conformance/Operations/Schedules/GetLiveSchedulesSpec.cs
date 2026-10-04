using Acta.Runtime;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for the live-schedule read every recurring fire and schedule verb makes: one slot's
/// schedules that are not orphaned, in name order, each with its effective and declared expression.
/// </summary>
[ConformanceSpec(
    "get-live-schedules.slot",
    "The live-schedule read returns a slot's non-orphaned schedules by name",
    Area = "Scheduling",
    Contract = "GetLiveSchedules returns one slot's non-orphaned schedules in name order, each with its effective and declared expression and time zone, cursor, and status."
)]
public sealed class GetLiveSchedulesSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const string DailyCron = "0 0 * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private Task<IReadOnlyList<LiveSchedule>> ReadAsync(long slotId, CancellationToken ct) =>
        Services.GetRequiredService<GetLiveSchedulesHandler>().HandleAsync(new GetLiveSchedulesQuery(slotId), ct);

    [Fact(DisplayName = "A slot's schedules read back in name order with their declaration, cursor, and status")]
    public async Task Schedules_read_back_in_name_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("live-order");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var slotId = await RegisterAsync(
            defId,
            jobName,
            [Slot("beta", Cron5, now.AddMinutes(5)), Slot("alpha", DailyCron, now.AddDays(1))],
            ct
        );

        var live = await ReadAsync(slotId, ct);

        Assert.Equal(["alpha", "beta"], live.Select(s => s.Name));
        var alpha = live[0];
        Assert.Equal(DailyCron, alpha.Expression);
        Assert.Equal(DailyCron, alpha.BaseExpression);
        Assert.Equal(alpha.BaseTimeZone, alpha.TimeZoneId);
        Assert.Equal(ScheduleExpressionKindCode.Cron, alpha.ExpressionKind);
        Assert.Equal(MisfireStrategyCode.Skip, alpha.MisfireStrategy);
        Assert.Equal(ScheduleStatusCode.Active, alpha.Status);
        Assert.Equal(now.AddDays(1), alpha.NextRunAtUtc);
        Assert.Equal(now.AddMinutes(5), live[1].NextRunAtUtc);
    }

    [Fact(DisplayName = "An override becomes the effective expression and time zone while the declared ones stay")]
    public async Task Override_is_effective_and_declaration_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("live-ovr");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var slotId = await RegisterAsync(defId, jobName, [Slot("only", Cron5, now.AddMinutes(5))], ct);
        var before = Assert.Single(await ReadAsync(slotId, ct));

        var set = await Schedules.UpdateOverridesAsync(Lookup(jobName, "only"), before.Version, DailyCron, "Europe/Ljubljana", ct: ct);
        Assert.Equal(ControlAction.Applied, set.Action);

        var after = Assert.Single(await ReadAsync(slotId, ct));
        Assert.Equal(DailyCron, after.Expression);
        Assert.Equal("Europe/Ljubljana", after.TimeZoneId);
        Assert.Equal(Cron5, after.BaseExpression);
        Assert.Equal(before.BaseTimeZone, after.BaseTimeZone);
        Assert.Equal(before.Version + 1, after.Version);
    }

    [Fact(DisplayName = "An orphaned schedule drops out while a paused one stays with its pause")]
    public async Task Orphaned_drops_out_and_paused_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = TestKey("live-orphan");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("kept", Cron5, now.AddMinutes(5)), Slot("gone", Cron5, now.AddMinutes(5))], ct);

        // Re-registering without "gone" orphans it.
        var slotId = await RegisterAsync(defId, jobName, [Slot("kept", Cron5, now.AddMinutes(5))], ct);
        var pausedUntil = now.AddHours(2);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(jobName, "kept"), pausedUntil, ct: ct)).Action);

        var kept = Assert.Single(await ReadAsync(slotId, ct));
        Assert.Equal("kept", kept.Name);
        Assert.Equal(ScheduleStatusCode.Paused, kept.Status);
        Assert.Equal(pausedUntil, kept.PausedUntilUtc);
    }

    [Fact(DisplayName = "A job that owns no schedules reads an empty list")]
    public async Task Job_without_schedules_reads_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (jobId, _) = await Seeder.SeedJobAsync(TestNamespaceId, ct: ct);

        Assert.Empty(await ReadAsync(jobId, ct));
    }

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    /// <summary>Whole seconds round-trip exactly through every provider's cursor column.</summary>
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private ScheduleLookup Lookup(string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), scheduleName);

    private static SlotSchedule Slot(string name, string expression, DateTime cursor) =>
        new(name, expression, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct) =>
        (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];

    private async Task<long> RegisterAsync(int defId, string jobName, IReadOnlyList<SlotSchedule> schedules, CancellationToken ct)
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            JobName: jobName,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            AuditLevel: DefinitionAuditLevelCode.Audit,
            SlotStatus: JobStatusCode.Ready,
            SlotMinNextRunAtUtc: schedules.Min(s => s.NextRunAtUtc),
            Schedules: schedules
        );
        return (await ScheduleTestOps.RegisterAsync(Services, [definition], ct))[defId];
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
