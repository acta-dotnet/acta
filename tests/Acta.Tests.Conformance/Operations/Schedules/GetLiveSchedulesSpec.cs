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

    private async Task<IReadOnlyList<LiveSchedule>> ReadAsync(long slotId, CancellationToken ct) =>
        (await Services.GetRequiredService<GetLiveSchedulesHandler>().HandleAsync(new GetLiveSchedulesQuery(slotId), ct)).Schedules;

    [Fact(DisplayName = "A slot's schedules read back in name order with their declaration, cursor, and status")]
    public async Task Schedules_read_back_in_name_order()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("live-order");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var slotId = await RegisterAsync(
            defId,
            jobName,
            [Slot("beta", Cron5, now.AddMinutes(5)), Slot("alpha", DailyCron, now.AddDays(1))],
            ct
        );

        var live = await ReadAsync(slotId, ct);

        Assert.Equal(["alpha", "beta"], live.Select(s => s.ScheduleName));
        var alpha = live[0];
        Assert.Equal(DailyCron, alpha.Expression);
        Assert.Equal(ScheduleExpressionKindCode.Cron, alpha.ExpressionKind);
        Assert.Equal(MisfireStrategyCode.Skip, alpha.MisfireStrategy);
        Assert.Equal(ScheduleStatusCode.Active, alpha.Status);
        Assert.Equal(now.AddDays(1), alpha.NextOccurrenceAtUtc);
        Assert.Equal(now.AddMinutes(5), live[1].NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "An orphaned schedule drops out while a paused one stays with its pause")]
    public async Task Orphaned_drops_out_and_paused_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("live-orphan");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("kept", Cron5, now.AddMinutes(5)), Slot("gone", Cron5, now.AddMinutes(5))], ct);

        // Re-registering without "gone" orphans it.
        var slotId = await RegisterAsync(defId, jobName, [Slot("kept", Cron5, now.AddMinutes(5))], ct);
        var pausedUntil = now.AddHours(2);
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(jobName, "kept"), pausedUntil, ct: ct)).Action);

        var kept = Assert.Single(await ReadAsync(slotId, ct));
        Assert.Equal("kept", kept.ScheduleName);
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

    private ScheduleLookup Lookup(string jobName, string scheduleName) => new(TestNamespace, jobName, scheduleName);

    private static SlotSchedule Slot(string name, string expression, DateTime cursor) =>
        new(name, expression, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct) =>
        (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];

    private async Task<long> RegisterAsync(int defId, string jobName, IReadOnlyList<SlotSchedule> schedules, CancellationToken ct)
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
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
            Priority: default,
            MaxAttempts: 1,
            EventRecording: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
