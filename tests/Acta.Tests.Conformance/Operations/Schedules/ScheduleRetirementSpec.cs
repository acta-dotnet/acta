using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// A retire leaves its definition's schedules rows in place, Active or paused as they were, and shows only on the
/// definition. Every schedule reader and writer therefore checks the definition, including a completion or an
/// operator verb that read the schedule before the retire committed and writes after it.
/// </summary>
[ConformanceSpec(
    "schedule.retired-inert",
    "A retired definition's schedules stay inert to every reader and writer",
    Area = "Scheduling",
    Contract = "After a retire, no reader returns its schedules, and no completion or verb, even one that read them first, writes their cursors or wakes their slot."
)]
public sealed class ScheduleRetirementSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int Attempt = 1;
    private const string Cron5 = "*/5 * * * *";
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override bool ParkScheduleSlots => false;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    [Fact(
        DisplayName = "A completion planned before the retire neither advances nor unpauses the retired schedules, nor wakes the slot for them"
    )]
    public async Task A_completion_planned_before_the_retire_leaves_the_schedules_inert()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var jobName = Unique("retired-fire");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("tick", cursor), Slot("lapsed", cursor), Slot("held", cursor)], ct);
        var slotId = await SlotIdAsync(jobName, ct);

        // A timed pause that has run out, which a fire would end, and one still running, which would wake the slot.
        await PauseUntilAsync(slotId, "lapsed", FloorSeconds(DateTime.UtcNow.AddHours(-1)), ct);
        var heldUntil = FloorSeconds(DateTime.UtcNow.AddHours(1));
        await PauseUntilAsync(slotId, "held", heldUntil, ct);
        var planned = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        var workerId = await LeaseInFlightAsync(slotId, ct);

        await RetireAsync(jobName, ct);
        Assert.Equal(JobStatusCode.Executing, (await ReadJobAsync(slotId, ct)).Status);

        var plannedNext = cursor.AddMinutes(5);
        var result = await CompleteAsync(
            slotId,
            workerId,
            [.. planned.Where(s => s.ScheduleName != "held").Select(s => new ScheduleAdvance(s.ScheduleName, plannedNext, s.Version))],
            ct
        );

        Assert.Equal(CompleteExecutionsAction.Completed, result.Action);
        Assert.Equal((byte)JobStatusCode.Paused, result.FinalJobStatusCode);
        Assert.Null(result.FinalNextRunAtUtc);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Paused, slot.Status);
        Assert.Null(slot.NextRunAtUtc);

        var after = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        Assert.All(after, s => Assert.Null(s.NextOccurrenceAtUtc));
        foreach (var before in planned)
        {
            var schedule = Assert.Single(after, s => s.ScheduleName == before.ScheduleName);
            Assert.Equal(before.Status, schedule.Status);
            Assert.Equal(before.PausedUntilUtc, schedule.PausedUntilUtc);
            Assert.Equal(before.Version, schedule.Version);
        }
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.SchedulePauseExpired, ct));
    }

    [Fact(DisplayName = "The slot's live schedules and a worker start's stored state leave out a retired definition's schedules")]
    public async Task Readers_leave_out_a_retired_definitions_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var jobName = Unique("retired-read");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var schedules = Services.GetRequiredService<SchedulesOperations>();
        Assert.Single((await schedules.GetLiveSchedulesAsync(slotId, ct)).Schedules);
        Assert.Contains((await schedules.GetScheduleStateAsync(TestNamespaceId, ct)).States, s => s.DefinitionId == defId);

        await RetireAsync(jobName, ct);

        Assert.Empty((await schedules.GetLiveSchedulesAsync(slotId, ct)).Schedules);
        Assert.DoesNotContain((await schedules.GetScheduleStateAsync(TestNamespaceId, ct)).States, s => s.DefinitionId == defId);
    }

    [Fact(DisplayName = "Resume and an override set that found the schedule before the retire write no cursor after it")]
    public async Task Resume_and_overrides_resolved_before_the_retire_write_no_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var resumeJob = Unique("retired-resume");
        var resumeSlot = await RegisterOneAsync(resumeJob, cursor, ct);
        var resume = Verb<ResumeScheduleHandler>("ResumeSchedule", () => RetireAsync(resumeJob, ct));
        var resumed = await resume.HandleAsync(new ResumeScheduleCommand(Lookup(resumeJob), "after the retire", null), ct);
        Assert.Equal(ControlAction.NotFound, resumed.Action);
        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, resumeSlot, ct)).NextOccurrenceAtUtc);

        var overridesJob = Unique("retired-overrides");
        var overridesSlot = await RegisterOneAsync(overridesJob, cursor, ct);
        var version = Assert.Single(await ScheduleTestOps.ReadAsync(Db, overridesSlot, ct)).Version;
        var overrides = Verb<SetScheduleOverridesHandler>("SetScheduleOverrides", () => RetireAsync(overridesJob, ct));
        var overridden = await overrides.HandleAsync(
            new SetScheduleOverridesCommand(Lookup(overridesJob), version, "*/10 * * * *", null, "after the retire", null),
            ct
        );
        Assert.Equal(ControlAction.NotFound, overridden.Action);
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, overridesSlot, ct));
        Assert.Null(schedule.NextOccurrenceAtUtc);
        Assert.Null(schedule.ExpressionOverride);
        Assert.Equal(version, schedule.Version);
    }

    [Fact(DisplayName = "A pause that found the schedule before the retire leaves the retired schedule as the retire left it")]
    public async Task A_pause_resolved_before_the_retire_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var jobName = Unique("retired-pause");
        var slotId = await RegisterOneAsync(jobName, cursor, ct);
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

        var pause = Verb<PauseScheduleHandler>("PauseSchedule", () => RetireAsync(jobName, ct));
        var paused = await pause.HandleAsync(new PauseScheduleCommand(Lookup(jobName), null, "after the retire", null), ct);

        Assert.Equal(ControlAction.NotFound, paused.Action);
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.SchedulePaused, ct));
    }

    [Fact(DisplayName = "A trigger that found the schedule before the retire leaves the parked slot of the retired definition parked")]
    public async Task A_trigger_resolved_before_the_retire_leaves_the_slot_parked()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var jobName = Unique("retired-trigger");
        var slotId = await RegisterOneAsync(jobName, cursor, ct);
        var planned = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        var workerId = await LeaseInFlightAsync(slotId, ct);

        // The retire leaves the running attempt alone, and its landing parks the slot with no next run, the one
        // state a trigger would otherwise make due.
        var trigger = Verb<TriggerScheduleNowHandler>(
            "TriggerScheduleNow",
            async () =>
            {
                await RetireAsync(jobName, ct);
                await CompleteAsync(slotId, workerId, [new ScheduleAdvance("only", cursor.AddMinutes(5), planned.Version)], ct);
            }
        );
        var triggered = await trigger.HandleAsync(new TriggerScheduleNowCommand(Lookup(jobName), "after the retire", null), ct);

        Assert.Equal(ControlAction.NotFound, triggered.Action);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Paused, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleTriggered, ct));
    }

    /// <summary>The verb handler as production builds it, over a session that retires the definition just before the verb's write.</summary>
    private THandler Verb<THandler>(string operation, Func<Task> retire) =>
        ActivatorUtilities.CreateInstance<THandler>(Services, new BeforeWriteSession(Db, operation, retire));

    private async Task RetireAsync(string jobName, CancellationToken ct)
    {
        var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);
        Assert.NotNull(definition);
        Assert.Equal(
            ControlAction.Applied,
            (await Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, ct: ct)).Action
        );
    }

    private Task PauseUntilAsync(long slotId, string scheduleName, DateTime untilUtc, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.schedules SET schedule_status_code = @p_status, paused_until_utc = @p_until, version = version + 1 "
                + "WHERE job_id = @p_id AND schedule_name = @p_name",
            ct,
            ("@p_status", (byte)ScheduleStatusCode.Paused),
            ("@p_until", DbValueCoercion.Coerce(untilUtc, typeof(DateTime), Db.Provider)),
            ("@p_id", slotId),
            ("@p_name", scheduleName)
        );

    private async Task<int> LeaseInFlightAsync(long jobId, CancellationToken ct)
    {
        // A real worker row, so the lease names an owner that exists and completion's owner check holds.
        var workers = await Db.From<WorkerEntity>().Where(w => w.NamespaceId == TestNamespaceId).ToListAsync(ct);
        Assert.NotEmpty(workers);
        var worker = workers[0];
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, execution_number = @p_attempt, leased_by_worker_id = @p_worker, "
                + "lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_attempt", Attempt),
            ("@p_worker", worker.Id),
            ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
            ("@p_id", jobId)
        );
        return worker.Id;
    }

    private Task<CompleteExecutionsRow> CompleteAsync(
        long jobId,
        int workerId,
        IReadOnlyList<ScheduleAdvance> advances,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<ExecutionOperations>()
            .CompleteAsync(
                workerId,
                new CompleteExecutionsValues(
                    jobId,
                    Attempt,
                    true,
                    JobPayloadFormat.None.Id,
                    ReadOnlyMemory<byte>.Empty,
                    EventCode.JobExecutionSucceeded,
                    null,
                    null,
                    null,
                    0,
                    null
                )
                {
                    ScheduleAdvances = advances,
                    FinalStatus = JobStatusCode.Ready,
                    JobNextRunAtUtc = advances.Count == 0 ? null : advances.Min(a => a.NextOccurrenceAtUtc),
                },
                ct
            );

    private async Task<long> RegisterOneAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        await RegisterAsync(await CreateDefinitionAsync(jobName, ct), jobName, cursor, [Slot("only", cursor)], ct);
        return await SlotIdAsync(jobName, ct);
    }

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private async Task RegisterAsync(
        int defId,
        string jobName,
        DateTime slotMin,
        IReadOnlyList<SlotSchedule> schedules,
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
            SlotStatus: JobStatusCode.Ready,
            SlotMinNextRunAtUtc: slotMin,
            Schedules: schedules
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct))?.JobId;
        Assert.NotNull(id);
        return id!.Value;
    }

    private ScheduleLookup Lookup(string jobName) => new(JobLookup.ByDeduplicationKey(TestNamespace, jobName), "only");

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private static SlotSchedule Slot(string name, DateTime cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

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

    /// <summary>
    /// Passes every call through to the real session, and runs <paramref name="before"/> once just before the
    /// <paramref name="operation"/> command reaches the database: the verb has read its schedule by then.
    /// </summary>
    private sealed class BeforeWriteSession(IDbSession inner, string operation, Func<Task> before) : IDbSession
    {
        private Func<Task>? _before = before;

        public DbProvider Provider => inner.Provider;

        public string Schema => inner.Schema;

        public Task<DbConnection> OpenConnectionAsync(CancellationToken ct) => inner.OpenConnectionAsync(ct);

        public Task<T> QueryAsync<T>(
            string stem,
            Action<DbCommand> bind,
            Func<DbDataReader, CancellationToken, Task<T>> read,
            CancellationToken ct
        ) => inner.QueryAsync(stem, bind, read, ct);

        public Task<IReadOnlyList<T>> ExecuteAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => inner.ExecuteAsync(command, bind, mapRow, ct);

        public Task<IReadOnlyList<T>> ExecuteInTransactionAsync<T>(
            DbTransaction transaction,
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        ) => inner.ExecuteInTransactionAsync(transaction, command, bind, mapRow, ct);

        public async Task<T?> ExecuteSingleAsync<T>(
            StoreCommand command,
            Action<DbCommand> bind,
            Func<DbDataReader, T> mapRow,
            CancellationToken ct
        )
            where T : class
        {
            if (command.Operation == operation && Interlocked.Exchange(ref _before, null) is { } hook)
            {
                await hook();
            }
            return await inner.ExecuteSingleAsync(command, bind, mapRow, ct);
        }

        public Task ExecuteAsync(StoreCommand command, Action<DbCommand> bind, CancellationToken ct) =>
            inner.ExecuteAsync(command, bind, ct);

        public Task<T> RunWithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct) =>
            inner.RunWithRetryAsync(action, ct);
    }
}
