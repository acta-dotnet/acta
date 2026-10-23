using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>Conformance for operator schedule resume: the cursor reconciles by the schedule's misfire policy.</summary>
[ConformanceSpec(
    "schedule.resume",
    "Resume reconciles a paused schedule's cursor by its misfire policy",
    Area = "Scheduling",
    Contract = "Resuming a schedule reconciles its cursor by misfire: Skip advances past now, and CatchUpOnce keeps the missed past instant."
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

    private ScheduleLookup Lookup(string jobName, string scheduleName) =>
        new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), scheduleName);

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
        DateTime? slotMin,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus,
        CancellationToken ct
    )
    {
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            JobName: jobName,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            SlotStatus: slotStatus,
            SlotMinNextRunAtUtc: slotMin,
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
        var (slotSchedules, slotStatus, slotNextRun) = ScheduleWalker.Reconcile(declared, storedForDef, now);
        await RegisterAsync(db, dialect, defId, jobName, slotNextRun, slotSchedules, slotStatus, ct);
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = (await Jobs().GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct))?.JobId;
        Assert.NotNull(id);
        return id!.Value;
    }

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
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    [Fact(DisplayName = "Resume reconciles the cursor by misfire: Skip advances past now and CatchUpOnce keeps the past instant")]
    public async Task Resume_with_skip_advances_past_now_and_fire_once_catch_up_keeps_the_past_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));

        // Skip: a missed cursor reconciles forward to the next occurrence after now.
        var skipJob = Unique("skip");
        var skipDef = await CreateDefinitionAsync(db, dialect, skipJob, ct);
        await RegisterAsync(
            db,
            dialect,
            skipDef,
            skipJob,
            now.AddMinutes(-37),
            [Slot("only", now.AddMinutes(-37), MisfireStrategyCode.Skip)],
            JobStatusCode.Ready,
            ct
        );
        await Schedules.PauseAsync(Lookup(skipJob, "only"), ct: ct);
        var skipResumed = await Schedules.ResumeAsync(Lookup(skipJob, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, skipResumed.Action);
        Assert.NotNull(skipResumed.NextOccurrenceAtUtc);
        Assert.True(skipResumed.NextOccurrenceAtUtc > now, "Skip resume advances strictly past now");

        // CatchUpOnce: the past instant is kept so the slot fires once on resume.
        var catchJob = Unique("catch");
        var catchDef = await CreateDefinitionAsync(db, dialect, catchJob, ct);
        var past = now.AddMinutes(-35); // a real */5 occurrence relative to a 5-minute aligned base is not required here
        await RegisterAsync(
            db,
            dialect,
            catchDef,
            catchJob,
            past,
            [Slot("only", past, MisfireStrategyCode.CatchUpOnce)],
            JobStatusCode.Ready,
            ct
        );
        await Schedules.PauseAsync(Lookup(catchJob, "only"), ct: ct);
        var catchResumed = await Schedules.ResumeAsync(Lookup(catchJob, "only"), ct: ct);
        Assert.Equal(ControlAction.Applied, catchResumed.Action);
        Assert.NotNull(catchResumed.NextOccurrenceAtUtc);
        Assert.True(catchResumed.NextOccurrenceAtUtc <= now, "CatchUpOnce resume keeps a past instant for one catch-up fire");
    }
}
