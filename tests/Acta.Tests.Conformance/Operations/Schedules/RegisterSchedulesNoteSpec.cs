using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>Conformance for catalog sync of schedule text: the attribute description is stored and an operator note survives re-sync.</summary>
[ConformanceSpec(
    "schedule.note-sync",
    "Catalog sync stores the description and keeps an operator note",
    Area = "Scheduling",
    Contract = "Registering scheduled jobs stores the attribute description with the note left NULL, and a later re-sync never overwrites an operator note."
)]
public sealed class RegisterSchedulesNoteSpec : ActaStorageTestBase<ConformanceFixture>
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
            AuditLevel: DefinitionAuditLevelCode.Audit,
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

    private async Task<ScheduleEntity> ScheduleAsync(IDbSession session, string jobName, string scheduleName, CancellationToken ct)
    {
        var slotId = await SlotIdAsync(jobName, ct);
        var rows = await session.From<ScheduleEntity>().Where(s => s.JobId == slotId && s.Name == scheduleName).ToListAsync(ct);
        return Assert.Single(rows);
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
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );

    [Fact(
        DisplayName = "Initial sync stores the attribute description with Note left NULL, and catalog re-sync does not overwrite an operator note"
    )]
    public async Task Sync_writes_description_and_resync_preserves_an_operator_note()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, dialect) = Store();
        var now = FloorSeconds(await NowAsync(ct));
        var jobName = Unique("desc");
        var defId = await CreateDefinitionAsync(db, dialect, jobName, ct);
        const string description = "Nightly cleanup of stale rows.";
        await RegisterAsync(
            db,
            dialect,
            defId,
            jobName,
            now.AddMinutes(5),
            [Slot("only", now.AddMinutes(5), description: description)],
            JobStatusCode.Ready,
            ct
        );

        var seeded = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(description, seeded.Description);
        Assert.Null(seeded.ReasonMessage);

        await Schedules.PauseAsync(Lookup(jobName, "only"), reasonMessage: "operator drain", ct: ct);

        // Re-run the startup catalog reconcile carrying the same declared description, exactly as
        // CatalogRegistrar.ReconcileSchedulesAsync does on every worker start.
        await ReReconcileAsync(db, dialect, defId, jobName, now, ct, description);

        var resynced = await ScheduleAsync(Db, jobName, "only", ct);
        Assert.Equal(description, resynced.Description); // description keeps tracking the attribute
        Assert.Equal("operator drain", resynced.ReasonMessage); // the operator note survives the re-sync
    }
}
