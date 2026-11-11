using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for who a schedule verb records: the operator it was given is its event's <c>actor</c> column on a slot's
/// schedule and on a per-occurrence one alike, and the details keep only the schedule's name.
/// </summary>
[ConformanceSpec(
    "schedule.actor",
    "A schedule verb records its operator as the event's actor",
    Area = "Scheduling",
    Contract = "The operator a schedule verb names is its event's actor, byte-identical on every provider, and the event's details name no actor."
)]
public sealed class ScheduleControlActorSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Operator = "Jože Kovač";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    [Theory(DisplayName = "The operator who applied a schedule verb is the event's actor, and the details name no actor")]
    [InlineData("pause", ScheduledDeliveryCode.Permanent, EventCode.SchedulePaused)]
    [InlineData("resume", ScheduledDeliveryCode.Permanent, EventCode.ScheduleResumed)]
    [InlineData("trigger", ScheduledDeliveryCode.Permanent, EventCode.ScheduleTriggered)]
    [InlineData("trigger", ScheduledDeliveryCode.PerOccurrence, EventCode.ScheduleTriggered)]
    public async Task Operator_is_the_events_actor(string verb, ScheduledDeliveryCode delivery, EventCode recorded)
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("actor");
        var definitionId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(jobName, delivery)], ct))[jobName];
        var cursor = (await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct)).AddHours(6);
        await ScheduleTestOps.RegisterAsync(
            Services,
            [
                new DefinitionSchedules(
                    NamespaceId: TestNamespaceId,
                    DefinitionId: definitionId,
                    InputFormatId: 0,
                    Input: ReadOnlyMemory<byte>.Empty,
                    ScheduledDelivery: delivery,
                    Schedules:
                    [
                        new SlotSchedule(
                            "only",
                            "*/5 * * * *",
                            null,
                            MisfireStrategyCode.Skip,
                            ScheduleExpressionKindCode.Cron,
                            null,
                            new DateTime(cursor.Ticks - (cursor.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)
                        ),
                    ]
                ),
            ],
            ct
        );
        var schedule = new ScheduleLookup(TestNamespace, jobName, "only");
        if (verb == "resume")
        {
            Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(schedule, ct: ct)).Action);
        }

        var action = verb switch
        {
            "pause" => (await Schedules.PauseAsync(schedule, reasonMessage: "why", actor: Operator, ct: ct)).Action,
            "resume" => (await Schedules.ResumeAsync(schedule, reasonMessage: "why", actor: Operator, ct: ct)).Action,
            _ => (await Schedules.TriggerAsync(schedule, reasonMessage: "why", actor: Operator, ct: ct)).Action,
        };

        Assert.Equal(ControlAction.Applied, action);
        var evt = Assert.Single(
            await Db.From<EventEntity>().Where(e => e.DefinitionId == definitionId && e.EventCode == recorded).ToListAsync(ct)
        );
        Assert.Equal(Operator, evt.Actor);
        Assert.Null(EventDetail(evt, "actor"));
        Assert.Equal("only", EventDetail(evt, "schedule"));
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
