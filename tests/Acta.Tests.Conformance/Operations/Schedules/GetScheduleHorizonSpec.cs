using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// The admitter's sleep bound: the namespace's earliest next evaluation, which a timed pause's end and an evaluation
/// retry are part of, and null once nothing timed is pending, which an indefinite pause takes a schedule out of.
/// </summary>
[ConformanceSpec(
    "schedule.horizon",
    "The horizon is the namespace's earliest next evaluation",
    Area = "Scheduling",
    Contract = "The horizon is the earliest next evaluation among the namespace's schedules, and null when none has one."
)]
public sealed class GetScheduleHorizonSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    [Fact(DisplayName = "The horizon follows the earliest next evaluation and is null once every schedule is paused indefinitely")]
    public async Task The_horizon_follows_the_earliest_next_evaluation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var sooner = Unique("sooner");
        var later = Unique("later");
        await RegisterAsync(sooner, now.AddHours(1), ct);
        await RegisterAsync(later, now.AddHours(2), ct);

        Assert.Equal(now.AddHours(1), await HorizonAsync(ct));

        // A timed pause ends on its own, so its end is the schedule's next evaluation.
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(sooner), untilUtc: now.AddMinutes(30), ct: ct)).Action);
        Assert.Equal(now.AddMinutes(30), await HorizonAsync(ct));

        // An indefinite pause takes the schedule out of the timed set.
        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(sooner), untilUtc: null, ct: ct)).Action);
        Assert.Equal(now.AddHours(2), await HorizonAsync(ct));

        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(Lookup(later), untilUtc: null, ct: ct)).Action);
        Assert.Null(await HorizonAsync(ct));
    }

    private async Task<DateTime?> HorizonAsync(CancellationToken ct) =>
        (
            await Services.GetRequiredService<GetScheduleHorizonHandler>().HandleAsync(new GetScheduleHorizonQuery(TestNamespaceId), ct)
        ).NextEvaluationAtUtc;

    private ScheduleLookup Lookup(string jobName) => new(TestNamespace, jobName, "only");

    private async Task RegisterAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules: [new SlotSchedule("only", Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor)]
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

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
