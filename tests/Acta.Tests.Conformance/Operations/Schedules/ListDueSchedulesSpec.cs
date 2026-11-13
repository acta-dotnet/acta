using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// The admitter's read: every member of each slot that has a schedule due for evaluation, carrying the slot's state
/// so a plan sees the whole slot, paged by evaluation instant, and never an orphaned schedule, a retired
/// definition's, or one that holds a code this build does not know.
/// </summary>
[ConformanceSpec(
    "schedule.due-schedules",
    "The due read returns whole slots in evaluation order and leaves out the dead",
    Area = "Scheduling",
    Contract = "A due read returns the members of each slot with a schedule due by now, in slot then name order, at most Take, less the orphaned, retired, and unreadable."
)]
public sealed class ListDueSchedulesSpec : ActaStorageTestBase<ConformanceFixture>
{
    private const string Cron5 = "*/5 * * * *";

    [Fact(DisplayName = "A slot with one due schedule is read whole, every member carrying the slot's state, in name order")]
    public async Task A_slot_with_a_due_schedule_is_read_whole()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("due");
        var slotId = await RegisterAsync(jobName, [("soon", now.AddMinutes(-1)), ("late", now.AddHours(1))], ct);

        var due = (await ReadAsync(now, 100, ct)).Schedules.Where(s => s.JobId == slotId).ToList();

        Assert.Equal(["late", "soon"], due.Select(s => s.ScheduleName));
        Assert.All(
            due,
            s =>
            {
                Assert.Equal(JobStatusCode.Idle, s.SlotStatus);
                Assert.Null(s.FiringStatus);
                Assert.Equal(jobName, s.DefinitionName);
                Assert.Equal(ScheduleEvaluationStatusCode.Scheduled, s.EvaluationStatus);
            }
        );
        Assert.Equal(now.AddMinutes(-1), due.Single(s => s.ScheduleName == "soon").NextEvaluationAtUtc);
        Assert.Equal(now.AddHours(1), due.Single(s => s.ScheduleName == "late").NextEvaluationAtUtc);
    }

    [Fact(DisplayName = "A slot with nothing due, an orphaned schedule, and a retired definition's schedule are left out")]
    public async Task Nothing_due_orphaned_and_retired_are_left_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);

        var futureJob = Unique("future");
        var futureSlot = await RegisterAsync(futureJob, [("only", now.AddHours(1))], ct);

        var orphanJob = Unique("orphan");
        var orphanSlot = await RegisterAsync(orphanJob, [("gone", now.AddMinutes(-1))], ct);
        await RegisterAsync(orphanJob, [], ct);

        var retiredJob = Unique("retired");
        var retiredSlot = await RegisterAsync(retiredJob, [("only", now.AddMinutes(-1))], ct);
        var definitions = Services.GetRequiredService<IActaOperations>().Definitions;
        var definition = await definitions.GetAsync(TestNamespace, retiredJob, ct);
        Assert.Equal(ControlAction.Applied, (await definitions.RetireAsync(TestNamespace, retiredJob, definition!.Version, ct: ct)).Action);

        var due = (await ReadAsync(now, 100, ct)).Schedules;

        Assert.DoesNotContain(due, s => s.JobId == futureSlot || s.JobId == orphanSlot || s.JobId == retiredSlot);
    }

    // A newer build shares the namespace during a rolling upgrade, and a slot can hold its schedules beside this one's.
    [Theory(DisplayName = "A member that holds a code this build does not know is left out, due or not, and its siblings are read")]
    [InlineData("schedule_status_code = 250")]
    [InlineData("expression_kind_code = 250")]
    [InlineData("misfire_strategy_code = 250")]
    [InlineData("full_behavior_code = 250")]
    [InlineData("expire_action_code = 250, expire_after_seconds = 60")]
    public async Task A_member_this_build_cannot_read_is_left_out(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);

        var siblingJob = Unique("sibling");
        var siblingSlot = await RegisterAsync(siblingJob, [("soon", now.AddMinutes(-1)), ("late", now.AddHours(1))], ct);
        await PlantAsync(siblingSlot, "late", unknown, ct);
        var aloneSlot = await RegisterAsync(Unique("alone"), [("soon", now.AddMinutes(-1))], ct);
        await PlantAsync(aloneSlot, "soon", unknown, ct);

        var due = (await ReadAsync(now, 100, ct)).Schedules;

        Assert.Equal(["soon"], due.Where(s => s.JobId == siblingSlot).Select(s => s.ScheduleName));
        Assert.DoesNotContain(due, s => s.JobId == aloneSlot);
    }

    private Task PlantAsync(long slotId, string scheduleName, string unknown, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            $"UPDATE {{schema}}.schedules SET {unknown} WHERE job_id = @p_id AND schedule_name = @p_name",
            ct,
            ("@p_id", slotId),
            ("@p_name", scheduleName)
        );

    [Fact(DisplayName = "The read pages by evaluation instant then id, at most Take due schedules per pass")]
    public async Task The_read_pages_by_evaluation_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var earliest = await RegisterAsync(Unique("page-a"), [("only", now.AddMinutes(-3))], ct);
        var middle = await RegisterAsync(Unique("page-b"), [("only", now.AddMinutes(-2))], ct);
        var latest = await RegisterAsync(Unique("page-c"), [("only", now.AddMinutes(-1))], ct);

        var page = (await ReadAsync(now, 2, ct)).Schedules.Select(s => s.JobId).ToList();

        Assert.Contains(earliest, page);
        Assert.Contains(middle, page);
        Assert.DoesNotContain(latest, page);
    }

    [Fact(
        DisplayName = "A backlogged schedule a pass decided is read behind the schedules with an earlier instant, so the page turns over"
    )]
    public async Task A_backlog_a_pass_decided_is_read_behind_the_schedules_with_an_earlier_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        // Two backlogs an hour deep fill the page. A pass drops one occurrence of each and leaves the next, still an
        // hour due; the third schedule came due a minute ago, later than anything the two have left.
        var backlogs = new[]
        {
            await RegisterBacklogAsync(Unique("backlog-a"), now.AddHours(-1), ct),
            await RegisterBacklogAsync(Unique("backlog-b"), now.AddHours(-1), ct),
        };
        var behind = await RegisterAsync(Unique("behind"), [("only", now.AddMinutes(-1))], ct);

        var page = (await ReadAsync(now, 2, ct)).Schedules;
        Assert.Equal(backlogs.Order(), page.Select(s => s.ScheduleId).Order());
        var resolve = await Services.GetRequiredService<CalendarsOperations>().PreloadAsync(TestNamespaceId, [], ct);
        var plan = ScheduleAdmitter.Plan(TestNamespaceId, page, now, resolve);
        var outcomes = (await Services.GetRequiredService<AdmitScheduleFiringsHandler>().HandleAsync(plan, ct)).Groups;

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Applied));
        Assert.DoesNotContain(plan.Groups, g => g.Admit);
        Assert.Contains((await ReadAsync(now, 2, ct)).Schedules, s => s.JobId == behind);
    }

    private Task<ListDueSchedulesResponse> ReadAsync(DateTime nowUtc, int take, CancellationToken ct) =>
        Services.GetRequiredService<ListDueSchedulesHandler>().HandleAsync(new ListDueSchedulesQuery(TestNamespaceId, nowUtc, take), ct);

    /// <summary>
    /// Registers a per-occurrence definition whose one per-minute CatchUpAll schedule sits at <paramref name="cursor"/>,
    /// full and dropping, so a pass decides one occurrence of it and admits nothing; returns the schedule's id.
    /// </summary>
    private async Task<int> RegisterBacklogAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        var descriptor = Def(jobName) with { ScheduledDelivery = ScheduledDeliveryCode.PerOccurrence };
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [descriptor], ct))[jobName];
        var schedule = new SlotSchedule(
            "only",
            "* * * * *",
            null,
            MisfireStrategyCode.CatchUpAll,
            ScheduleExpressionKindCode.Cron,
            null,
            cursor,
            FullBehavior: ScheduleFullBehaviorCode.Drop
        );
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            ScheduledDelivery: ScheduledDeliveryCode.PerOccurrence,
            Schedules: [schedule]
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
        var scheduleId = (await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == defId).SingleOrDefaultAsync(ct))!.Id;
        // One root is outstanding, which is the schedule's whole capacity.
        Assert.Equal(
            1,
            await Db.From<ScheduleStateEntity>()
                .Where(s => s.Id == scheduleId)
                .UpdateOnlyAsync(() => new ScheduleStateEntity { OutstandingCount = 1 }, ct)
        );
        return scheduleId;
    }

    /// <summary>Registers (or re-registers) one definition's schedules at the given cursors; returns its slot.</summary>
    private async Task<long> RegisterAsync(string jobName, IReadOnlyList<(string Name, DateTime Cursor)> schedules, CancellationToken ct)
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, [Def(jobName)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules:
            [
                .. schedules.Select(s => new SlotSchedule(
                    s.Name,
                    Cron5,
                    null,
                    MisfireStrategyCode.Skip,
                    ScheduleExpressionKindCode.Cron,
                    null,
                    s.Cursor
                )),
            ]
        );
        return (await ScheduleTestOps.RegisterAsync(Services, [definition], ct))[defId];
    }

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

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
            EventRecording: EventRecordingCode.Failures,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeResult: null
        );
}
