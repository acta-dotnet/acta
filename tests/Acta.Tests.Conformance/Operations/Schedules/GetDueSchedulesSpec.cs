using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// The admitter's read: every member of each slot that has a schedule due for evaluation, carrying the slot's state
/// so a plan sees the whole slot, paged by evaluation instant, and never an orphaned schedule or a retired
/// definition's.
/// </summary>
[ConformanceSpec(
    "schedule.due-schedules",
    "The due read returns whole slots in evaluation order and leaves out the dead",
    Area = "Scheduling",
    Contract = "A due read returns every member of each slot with a schedule due by now, in slot then name order, at most Take due schedules, less the orphaned and retired."
)]
public sealed class GetDueSchedulesSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
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
                Assert.Equal(EvaluationStatusCode.Scheduled, s.EvaluationStatus);
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

    private Task<GetDueSchedulesResponse> ReadAsync(DateTime nowUtc, int take, CancellationToken ct) =>
        Services.GetRequiredService<GetDueSchedulesHandler>().HandleAsync(new GetDueSchedulesQuery(TestNamespaceId, nowUtc, take), ct);

    /// <summary>Registers (or re-registers) one definition's schedules at the given cursors; returns its slot.</summary>
    private async Task<long> RegisterAsync(string jobName, IReadOnlyList<(string Name, DateTime Cursor)> schedules, CancellationToken ct)
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
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
