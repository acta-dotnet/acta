using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>Conformance for operator schedule resume: the remembered cursor stays, and the admitter decides it under the misfire policy at its next pass.</summary>
[ConformanceSpec(
    "schedule.resume",
    "Resume returns a paused schedule to the admitter with its cursor kept",
    Area = "Scheduling",
    Contract = "Resuming a schedule keeps its cursor, marks it for evaluation now, and leaves the misfire decision to the admitter's next pass."
)]
public sealed class ResumeScheduleSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private ISchedules Schedules => Services.GetRequiredService<IActaOperations>().Schedules;

    private (IDbSession Db, ISqlDialect Dialect) Store() => (Db, Services.GetRequiredService<ISqlDialect>());

    // Floor to whole seconds so synthetic cursors round-trip exactly through every provider's catalog
    // cursor storage (SQL Server binds catalog cursors at DATETIME2(3) while its clock is DATETIME2(7)).
    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private async Task<DateTime> NowAsync(CancellationToken ct) => await Services.GetRequiredService<IActaClock>().GetUtcNowAsync(ct);

    private ScheduleLookup Lookup(string jobName, string scheduleName) => new(TestNamespace, jobName, scheduleName);

    private static SlotSchedule Slot(
        string name,
        DateTime? cursor,
        MisfireStrategyCode misfire = MisfireStrategyCode.Skip,
        string? description = null
    ) => new(name, Cron5, null, misfire, ScheduleExpressionKindCode.Cron, description, cursor);

    private async Task<int> CreateDefinitionAsync(IDbSession db, ISqlDialect dialect, string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private async Task RegisterAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        IReadOnlyList<SlotSchedule> schedules,
        CancellationToken ct
    )
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
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private async Task ReReconcileAsync(
        IDbSession db,
        ISqlDialect dialect,
        int defId,
        string jobName,
        DateTime now,
        CancellationToken ct,
        string? description = null
    )
    {
        var stored = (
            await Services.GetRequiredService<GetScheduleStateHandler>().HandleAsync(new GetScheduleStateQuery(TestNamespaceId), ct)
        ).States;
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);
        var declared = new[]
        {
            new ScheduleDescriptor(
                jobName,
                "only",
                Cron5,
                null,
                MisfireStrategyCode.Skip,
                ScheduleExpressionKindCode.Cron,
                description,
                []
            ),
        };
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            ScheduleRegistrationPlan.Plan(declared, storedForDef, now, CalendarRegistry.Empty.Source),
            ct
        );
    }

    private Task<long> SlotIdAsync(string jobName, CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, jobName, ct);

    private IJobs Jobs() => Services.GetRequiredService<IJobs>();

    private async Task<ScheduleRow> ScheduleAsync(IDbSession session, string jobName, string scheduleName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        return Assert.Single(await ScheduleTestOps.ReadAsync(session, slotId, ct), s => s.ScheduleName == scheduleName);
    }

    private async Task<TestJobRow> SlotAsync(string jobName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        return await ReadJobAsync(slotId, ct);
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
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    [Theory(
        DisplayName = "Resume keeps the remembered cursor, whatever the misfire policy, and puts the schedule back in the admitter's seek now"
    )]
    [InlineData(MisfireStrategyCode.Skip)]
    [InlineData(MisfireStrategyCode.CatchUpOnce)]
    public async Task Resume_keeps_the_cursor_and_returns_the_schedule_to_the_admitter(MisfireStrategyCode misfire)
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique(misfire == MisfireStrategyCode.Skip ? "skip" : "catch");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        var missed = now.AddMinutes(-35);
        await RegisterAsync(db, dialect, defId, jobName, [Slot("only", missed, misfire)], ct);
        await Schedules.PauseAsync(Lookup(jobName, "only"), ct: ct);

        var resumed = await Schedules.ResumeAsync(Lookup(jobName, "only"), ct: ct);

        Assert.Equal(ControlAction.Applied, resumed.Action);
        Assert.Equal(ScheduleStatusCode.Active, resumed.Status);
        // The missed occurrence is the admitter's to decide under the policy at its next pass, not the verb's.
        Assert.Equal(missed, resumed.NextOccurrenceAtUtc);
        var schedule = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(missed, schedule.NextOccurrenceAtUtc);
        Assert.Equal(EvaluationStatusCode.Resumed, schedule.EvaluationStatus);
        Assert.InRange(schedule.NextEvaluationAtUtc!.Value, now, now.AddMinutes(1));
        var slot = await SlotAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
    }
}
