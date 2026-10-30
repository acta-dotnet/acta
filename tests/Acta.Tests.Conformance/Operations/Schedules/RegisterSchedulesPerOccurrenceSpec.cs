using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Conformance for a PerOccurrence definition's schedules: registration writes them with no slot and with their
/// declared capacity, expiry, and availability calendar, and the schedule verbs address them by path, auditing against the
/// definition.
/// </summary>
[ConformanceSpec(
    "schedule.per-occurrence-registration",
    "PerOccurrence schedules register without a slot and are addressed by path",
    Area = "Scheduling",
    Contract = "PerOccurrence schedules register with no slot and their capacity, expiry, and availability, and the verbs address them by path and audit the definition."
)]
public sealed class RegisterSchedulesPerOccurrenceSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    [Fact(DisplayName = "A PerOccurrence definition's schedules register with no slot, their declared capacity, and nothing outstanding")]
    public async Task A_per_occurrence_definition_registers_its_schedules_without_a_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("roots");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var slots = await RegisterAsync(defId, [Slot("only", cursor, 3, ScheduleFullBehaviorCode.Drop)], ct);

        Assert.DoesNotContain(defId, slots.Keys);
        Assert.Null(await ScheduleTestOps.TrySlotIdAsync(Db, TestNamespaceId, jobName, ct));
        var (config, state) = await ScheduleAsync(defId, "only", ct);
        Assert.Null(config.JobId);
        Assert.Equal(3, config.MaxOutstanding);
        Assert.Equal(ScheduleFullBehaviorCode.Drop, config.FullBehavior);
        Assert.Equal(0, state.OutstandingCount);
        Assert.Equal(cursor, state.NextOccurrenceAtUtc);

        // A changed capacity registers in place, and a schedule the next start drops is orphaned through its definition.
        await RegisterAsync(defId, [Slot("only", cursor, 5, ScheduleFullBehaviorCode.Wait), Slot("other", cursor)], ct);
        (config, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal(5, config.MaxOutstanding);
        Assert.Equal(ScheduleFullBehaviorCode.Wait, config.FullBehavior);

        await RegisterAsync(defId, [Slot("other", cursor)], ct);
        (config, state) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, config.Status);
        Assert.Null(state.NextOccurrenceAtUtc);
        Assert.Null((await ScheduleAsync(defId, "other", ct)).Config.JobId);
    }

    [Fact(
        DisplayName = "A schedule's expiry and misfire strategy register, a changed expiry re-registers in place, and a plain restart writes nothing"
    )]
    public async Task A_schedule_registers_its_expiry_and_changes_it_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("roots");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var expiring = Slot("only", FloorSeconds(DateTime.UtcNow.AddHours(6))) with
        {
            MisfireStrategy = MisfireStrategyCode.CatchUpAll,
            ExpireAfterSeconds = 3600,
            ExpireAction = ScheduleExpireActionCode.Discard,
        };

        await RegisterAsync(defId, [expiring], ct);
        var (config, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal(MisfireStrategyCode.CatchUpAll, config.MisfireStrategy);
        Assert.Equal(3600, config.ExpireAfterSeconds);
        Assert.Equal(ScheduleExpireActionCode.Discard, config.ExpireAction);

        // The stored state reads back as the plan, so a restart of the same build skips the registration.
        Assert.True(CatalogRegistrar.IsRegistered(Plan(defId, [expiring]), await StoredAsync(defId, ct), JobPriorityCode.Normal));
        await RegisterAsync(defId, [expiring], ct);
        Assert.Equal(config.Version, (await ScheduleAsync(defId, "only", ct)).Config.Version);

        var paused = expiring with { ExpireAfterSeconds = 60, ExpireAction = ScheduleExpireActionCode.Pause };
        Assert.False(CatalogRegistrar.IsRegistered(Plan(defId, [paused]), await StoredAsync(defId, ct), JobPriorityCode.Normal));
        await RegisterAsync(defId, [paused], ct);
        var (changed, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal(config.Id, changed.Id);
        Assert.Equal(config.Version + 1, changed.Version);
        Assert.Equal(60, changed.ExpireAfterSeconds);
        Assert.Equal(ScheduleExpireActionCode.Pause, changed.ExpireAction);

        await RegisterAsync(defId, [expiring with { ExpireAfterSeconds = null, ExpireAction = null }], ct);
        (changed, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Null(changed.ExpireAfterSeconds);
        Assert.Null(changed.ExpireAction);
    }

    [Fact(
        DisplayName = "A schedule's availability calendar registers and reads back, a changed name re-registers in place, and a plain restart writes nothing"
    )]
    public async Task A_schedule_registers_its_availability_calendar_and_changes_it_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("roots");
        var defId = await CreateDefinitionAsync(jobName, ct);
        var available = Slot("only", FloorSeconds(DateTime.UtcNow.AddHours(6))) with { AvailabilityCalendarName = "office-hours" };

        await RegisterAsync(defId, [available], ct);
        var (config, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal("office-hours", config.AvailabilityCalendarName);
        var stored = (await StoredAsync(defId, ct))["only"];
        Assert.Equal("office-hours", stored.AvailabilityCalendarName);
        var listed = Assert.Single(
            (await Schedules.ListAsync(new ListSchedulesQuery(JobNamespace: TestNamespace, JobName: jobName, LiveOnly: false), ct)).Items
        );
        Assert.Equal("office-hours", listed.AvailabilityCalendarName);

        // The stored state reads back as the plan, so a restart of the same build skips the registration.
        Assert.True(CatalogRegistrar.IsRegistered(Plan(defId, [available]), await StoredAsync(defId, ct), JobPriorityCode.Normal));
        await RegisterAsync(defId, [available], ct);
        Assert.Equal(config.Version, (await ScheduleAsync(defId, "only", ct)).Config.Version);

        var renamed = available with { AvailabilityCalendarName = "support-hours" };
        Assert.False(CatalogRegistrar.IsRegistered(Plan(defId, [renamed]), await StoredAsync(defId, ct), JobPriorityCode.Normal));
        await RegisterAsync(defId, [renamed], ct);
        var (changed, _) = await ScheduleAsync(defId, "only", ct);
        Assert.Equal(config.Id, changed.Id);
        Assert.Equal(config.Version + 1, changed.Version);
        Assert.Equal("support-hours", changed.AvailabilityCalendarName);
    }

    [Fact(
        DisplayName = "A slot-less schedule resolves by path, pauses and resumes with the audit on its definition, and triggers a manual root"
    )]
    public async Task A_slotless_schedule_is_addressed_by_its_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("roots");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, [Slot("only", FloorSeconds(DateTime.UtcNow.AddHours(6)))], ct);
        var (config, _) = await ScheduleAsync(defId, "only", ct);
        var lookup = new ScheduleLookup(TestNamespace, jobName, "only");

        var target = await Services
            .GetRequiredService<ResolveScheduleTargetHandler>()
            .HandleAsync(new ResolveScheduleTargetQuery(lookup), ct);
        Assert.NotNull(target);
        Assert.Equal(config.Id, target.ScheduleId);
        Assert.Null(target.JobId);
        Assert.Equal(defId, target.DefinitionId);

        Assert.Equal(ControlAction.Applied, (await Schedules.PauseAsync(lookup, reasonMessage: "hold", ct: ct)).Action);
        Assert.Equal(ScheduleStatusCode.Paused, (await ScheduleAsync(defId, "only", ct)).Config.Status);
        Assert.Equal(ControlAction.Applied, (await Schedules.ResumeAsync(lookup, ct: ct)).Action);
        Assert.Equal(ScheduleStatusCode.Active, (await ScheduleAsync(defId, "only", ct)).Config.Status);
        Assert.Equal(ControlAction.Applied, (await Schedules.TriggerNowAsync(lookup, ct: ct)).Action);
        var root = Assert.Single(await Db.From<JobEntity>().Where(j => j.ScheduleId == config.Id).ToListAsync(ct));
        Assert.StartsWith("sys.manual.", root.DeduplicationKey, StringComparison.Ordinal);
        Assert.Equal(1, (await ScheduleAsync(defId, "only", ct)).State.OutstandingCount);

        long definitionEntityId = defId;
        var audited = (
            await Db.From<EventEntity>()
                .Where(e => e.EntityCode == EntityCode.Definition && e.EntityId == definitionEntityId)
                .ToListAsync(ct)
        )
            .Where(e => e.EventCode is EventCode.SchedulePaused or EventCode.ScheduleResumed or EventCode.ScheduleTriggered)
            .OrderBy(e => e.Id)
            .ToList();
        Assert.Equal([EventCode.SchedulePaused, EventCode.ScheduleResumed], audited.Select(e => e.EventCode));
        Assert.All(audited, e => Assert.Equal(defId, e.DefinitionId));
    }

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private Task<IReadOnlyDictionary<int, long>> RegisterAsync(int defId, IReadOnlyList<SlotSchedule> schedules, CancellationToken ct) =>
        ScheduleTestOps.RegisterAsync(Services, [Plan(defId, schedules)], ct);

    private DefinitionSchedules Plan(int defId, IReadOnlyList<SlotSchedule> schedules) =>
        new(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Schedules: schedules
        );

    private async Task<Dictionary<string, StoredScheduleState>> StoredAsync(int defId, CancellationToken ct) =>
        (await Services.GetRequiredService<GetScheduleStateHandler>().HandleAsync(new GetScheduleStateQuery(TestNamespaceId), ct))
            .States.Where(s => s.DefinitionId == defId)
            .ToDictionary(s => s.ScheduleName, StringComparer.Ordinal);

    private async Task<(ScheduleEntity Config, ScheduleStateEntity State)> ScheduleAsync(int defId, string name, CancellationToken ct)
    {
        var config = await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == defId && s.ScheduleName == name).SingleOrDefaultAsync(ct);
        Assert.NotNull(config);
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == config.Id).SingleOrDefaultAsync(ct);
        Assert.NotNull(state);
        return (config, state);
    }

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private static SlotSchedule Slot(
        string name,
        DateTime cursor,
        int maxOutstanding = 1,
        ScheduleFullBehaviorCode fullBehavior = ScheduleFullBehaviorCode.Wait
    ) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor, null, maxOutstanding, fullBehavior);

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
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
