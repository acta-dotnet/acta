using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for a schedule's bounds: registration stores them, the point read answers them, they are timing, a schedule
/// past its end registers exhausted and a later end brings it back, and a trigger fires whatever the bounds.
/// </summary>
[ConformanceSpec(
    "schedule.registration-bounds",
    "Registration stores a schedule's bounds as part of its timing",
    Area = "Scheduling",
    Contract = "Registration stores the bounds the read answers, a changed bound retimes, a schedule past its end is exhausted, and a trigger fires it anyway."
)]
public sealed class RegisterSchedulesBoundsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Start = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndBefore = new(2028, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const string ScheduleName = "only";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    [Fact(DisplayName = "A schedule's bounds are stored and read back, and dropping them is a timing change")]
    public async Task Bounds_are_stored_read_back_and_fingerprinted()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("bounds");
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        var defId = map[jobName];
        var lookup = new ScheduleLookup(TestNamespace, jobName, ScheduleName);

        var slotId = await PlanAndRegisterAsync(defId, jobName, Start, EndBefore, ct);
        var bounded = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        var read = await Schedules.GetAsync(lookup, ct);

        Assert.NotNull(read);
        Assert.Equal(Start, read.StartAtUtc);
        Assert.Equal(EndBefore, read.EndBeforeUtc);
        Assert.Equal(Start, bounded.Config.StartAtUtc);
        Assert.Equal(EndBefore, bounded.Config.EndBeforeUtc);

        await PlanAndRegisterAsync(defId, jobName, null, null, ct);
        var unbounded = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        var reread = await Schedules.GetAsync(lookup, ct);

        Assert.NotNull(reread);
        Assert.Null(reread.StartAtUtc);
        Assert.Null(reread.EndBeforeUtc);
        Assert.NotEqual(bounded.Config.TimingFingerprint, unbounded.Config.TimingFingerprint);
        Assert.True(unbounded.Version > bounded.Version, "A change to the bounds rewrites the schedule row.");
    }

    [Fact(DisplayName = "A schedule past its end registers exhausted, and a build that extends the end retimes it and brings it back")]
    public async Task A_schedule_past_its_end_registers_exhausted_until_a_later_end_brings_it_back()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("ended");
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        var defId = map[jobName];

        // The end is the registration instant itself, so no occurrence is left.
        var slotId = await PlanAndRegisterAsync(defId, jobName, null, Generation, ct);
        var ended = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Null(ended.NextOccurrenceAtUtc);
        Assert.Null(ended.NextEvaluationAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Exhausted, ended.EvaluationStatus);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));

        await PlanAndRegisterAsync(defId, jobName, null, Generation.AddHours(1), ct);
        var extended = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(Generation.AddMinutes(5), extended.NextOccurrenceAtUtc);
        Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, extended.EvaluationStatus);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));
    }

    [Fact(
        DisplayName = "A trigger fires a schedule past its end, since the bounds are about the cadence and a trigger is an operator's act"
    )]
    public async Task A_trigger_fires_a_schedule_past_its_end()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("trigger-ended");
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        var slotId = await PlanAndRegisterAsync(map[jobName], jobName, null, Generation, ct);

        var result = await Schedules.TriggerAsync(
            new ScheduleLookup(TestNamespace, jobName, ScheduleName),
            reasonMessage: "once more",
            ct: ct
        );

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.NotNull(result.JobRef);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
    }

    // The startup insert path, as CatalogRegistrar.ReconcileSchedulesAsync runs it: read the stored states, plan, upsert.
    private async Task<long> PlanAndRegisterAsync(
        int defId,
        string jobName,
        DateTime? startAtUtc,
        DateTime? endBeforeUtc,
        CancellationToken ct
    )
    {
        var stored = (
            await Services.GetRequiredService<ListScheduleStatesHandler>().HandleAsync(new ListScheduleStatesQuery(TestNamespaceId), ct)
        ).States;
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);
        var declared = new[]
        {
            new ScheduleDescriptor(
                jobName,
                ScheduleName,
                Cron5,
                null,
                MisfireStrategyCode.Skip,
                ScheduleExpressionKindCode.Cron,
                null,
                [],
                StartAtUtc: startAtUtc,
                EndBeforeUtc: endBeforeUtc
            ),
        };
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules: ScheduleRegistrationPlan.Plan(declared, storedForDef, Generation, t => new ScheduleSource(t, null, null))
        );
        var ids = await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
        return ids[defId];
    }

    private static JobDescriptor Def(string name) =>
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
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );
}
