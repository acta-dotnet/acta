using System.Data.Common;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Schedule writers serialize on their namespace's schedule lock (docs/sql-execution-policy.md, "Schedule lock"):
/// registration and retire hold it exclusive, a slot fire's completion and the schedule verbs hold it shared. A gate
/// connection stands in for a registration by holding the lock exclusive in an open transaction, with its uncommitted
/// edits, or for a fire by holding it shared. Deadlock retry is off, so a lock-order inversion fails the races instead
/// of being retried. SQLite runs one writer at a time, so nothing here can interleave there.
/// </summary>
[ConformanceSpec(
    "schedule.lock-order",
    "Schedule writers serialize on the namespace's schedule lock",
    Area = "Scheduling",
    Contract = "Fires and schedule verbs share the namespace's schedule lock that registration and retire hold alone, so no write acts on config another has not committed."
)]
public sealed class ScheduleLockOrderSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
{
    private const int Rounds = 20;
    private const int Attempt = 1;
    private const string Cron5 = "*/5 * * * *";
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    protected override bool ParkScheduleSlots => false;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        base.ConfigureServices(services, testNamespace);
        var provider = services.Last(d => d.ServiceType == typeof(SqlProviderOptions));
        services.Remove(provider);
        services.AddSingleton(sp =>
        {
            var options = (SqlProviderOptions)provider.ImplementationFactory!(sp);
            DeadlockRetryOff.Apply(sp, options);
            return options;
        });
    }

    [Fact(
        DisplayName = "A completion that waits on a registration's uncommitted schedule edit re-reads the version once it commits and keeps it"
    )]
    public async Task A_completion_waiting_on_a_schedule_edit_refuses_its_stale_advance()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var edited = FloorSeconds(DateTime.UtcNow.AddHours(2));
        var jobName = Unique("lock-edit");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var read = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        var workerId = await LeaseInFlightAsync(slotId, ct);

        // A registration in flight: the schedule lock, then the schedules row and its cursor, all uncommitted.
        await using var gateConnection = await Db.OpenConnectionAsync(ct);
        await using var gate = await gateConnection.BeginTransactionAsync(ct);
        await ScheduleLockGate.HoldAsync(Db, gateConnection, gate, TestNamespaceId, exclusive: true, ct);
        await GateAsync(gateConnection, gate, "schedules", "version = version + 1", slotId, null, ct);
        await GateAsync(gateConnection, gate, "schedules_state", "next_occurrence_at_utc = @p_at", slotId, edited, ct);

        var plannedNext = cursor.AddMinutes(5);
        var completion = CompleteAsync(slotId, workerId, [new ScheduleAdvance("only", plannedNext, read.Version)], plannedNext, ct);
        await Task.Delay(Blocked, ct);
        Assert.False(completion.IsCompleted, "the completion did not wait for the registration");
        await gate.CommitAsync(ct);

        var result = await completion.WaitAsync(SpecWaits.Gate, ct);
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(read.Version + 1, after.Version);
        Assert.Equal(edited, after.NextOccurrenceAtUtc);
        Assert.Equal((byte)JobStatusCode.Ready, result.FinalJobStatusCode);
        Assert.Equal(edited, result.FinalNextRunAtUtc);
    }

    [Fact(
        DisplayName = "A slot-firing completion waits for a held exclusive schedule lock, and two slot fires in one namespace do not block each other"
    )]
    public async Task Fires_wait_for_a_registration_and_never_for_each_other()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var plannedNext = cursor.AddMinutes(5);

        var (firstSlot, firstWorker, firstRead) = await InFlightSlotAsync(Unique("lock-held"), cursor, ct);
        await using (var gateConnection = await Db.OpenConnectionAsync(ct))
        await using (var gate = await gateConnection.BeginTransactionAsync(ct))
        {
            await ScheduleLockGate.HoldAsync(Db, gateConnection, gate, TestNamespaceId, exclusive: true, ct);
            var waiting = CompleteAsync(
                firstSlot,
                firstWorker,
                [new ScheduleAdvance("only", plannedNext, firstRead.Version)],
                plannedNext,
                ct
            );
            await Task.Delay(Blocked, ct);
            Assert.False(waiting.IsCompleted, "the fire did not wait for the exclusive schedule lock");
            await gate.RollbackAsync(ct);
            Assert.Equal(CompleteExecutionsAction.Completed, (await waiting.WaitAsync(SpecWaits.Gate, ct)).Action);
        }

        // The gate holds the lock shared, as another fire's completion in the namespace does until it commits.
        var (secondSlot, secondWorker, secondRead) = await InFlightSlotAsync(Unique("lock-shared"), cursor, ct);
        await using (var gateConnection = await Db.OpenConnectionAsync(ct))
        await using (var gate = await gateConnection.BeginTransactionAsync(ct))
        {
            await ScheduleLockGate.HoldAsync(Db, gateConnection, gate, TestNamespaceId, exclusive: false, ct);
            var fire = await CompleteAsync(
                    secondSlot,
                    secondWorker,
                    [new ScheduleAdvance("only", plannedNext, secondRead.Version)],
                    plannedNext,
                    ct
                )
                .WaitAsync(SpecWaits.Gate, ct);
            Assert.Equal(CompleteExecutionsAction.Completed, fire.Action);
            Assert.Equal(plannedNext, Assert.Single(await ScheduleTestOps.ReadAsync(Db, secondSlot, ct)).NextOccurrenceAtUtc);

            // The shared hold is real: a registration waits behind it.
            var registration = RegisterOtherAsync(Unique("lock-shared-other"), cursor, ct);
            await Task.Delay(Blocked, ct);
            Assert.False(registration.IsCompleted, "the registration did not wait for the shared schedule lock");
            await gate.RollbackAsync(ct);
            await registration.WaitAsync(SpecWaits.Gate, ct);
        }
    }

    [Fact(DisplayName = "The registration heal cannot clear a schedule a concurrent registration revives, because the two serialize")]
    public async Task The_heal_waits_for_a_revival_and_leaves_the_revived_cursor()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var revived = FloorSeconds(DateTime.UtcNow.AddHours(3));
        var slotId = await OrphanWithStaleCursorAsync(Unique("heal-revived"), cursor, ct);

        // A reviving registration in flight: the schedule lock, the schedule back to Active, and its new cursor.
        await using var gateConnection = await Db.OpenConnectionAsync(ct);
        await using var gate = await gateConnection.BeginTransactionAsync(ct);
        await ScheduleLockGate.HoldAsync(Db, gateConnection, gate, TestNamespaceId, exclusive: true, ct);
        await GateAsync(gateConnection, gate, "schedules", "schedule_status_code = 10, version = version + 1", slotId, null, ct);
        await GateAsync(gateConnection, gate, "schedules_state", "next_occurrence_at_utc = @p_at", slotId, revived, ct);

        // Any registration in the namespace runs the heal; it waits until the revival commits.
        var healing = RegisterOtherAsync(Unique("heal-other"), cursor, ct);
        await Task.Delay(Blocked, ct);
        Assert.False(healing.IsCompleted, "the healing registration did not wait for the revival");
        await gate.CommitAsync(ct);
        await healing.WaitAsync(SpecWaits.Gate, ct);

        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Active, schedule.Status);
        Assert.Equal(revived, schedule.NextOccurrenceAtUtc);
    }

    [Fact(
        DisplayName = "A registration that lands after a retire of a definition it registers leaves the retired schedules with no cursor"
    )]
    public async Task A_registration_after_a_retire_leaves_no_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var jobName = Unique("retired-registered");
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);
        Assert.Equal(
            ControlAction.Applied,
            (await Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, ct: ct)).Action
        );

        // A worker that read the stored schedules before the retire registers the cursor it planned then.
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);

        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "A registration racing a fire's completion never deadlocks, and the registered cursor stands either way")]
    public async Task Registration_and_completion_race_without_deadlock()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
            var declared = FloorSeconds(DateTime.UtcNow.AddHours(2));
            var jobName = Unique($"race-register-{round}");
            var defId = await CreateDefinitionAsync(jobName, ct);
            await RegisterAsync(defId, jobName, cursor, [Slot("a", cursor), Slot("b", cursor)], JobStatusCode.Ready, ct);
            var slotId = await SlotIdAsync(jobName, ct);
            var read = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
            var workerId = await LeaseInFlightAsync(slotId, ct);

            var plannedNext = cursor.AddMinutes(5);
            var completion = CompleteAsync(
                slotId,
                workerId,
                [.. read.Select(s => new ScheduleAdvance(s.ScheduleName, plannedNext, s.Version))],
                plannedNext,
                ct
            );
            var registration = RegisterAsync(defId, jobName, declared, [Slot("a", declared), Slot("b", declared)], JobStatusCode.Ready, ct);
            await Task.WhenAll(completion, registration);

            Assert.Equal(CompleteExecutionsAction.Completed, (await completion).Action);
            Assert.All(await ScheduleTestOps.ReadAsync(Db, slotId, ct), s => Assert.Equal(declared, s.NextOccurrenceAtUtc));
        }
    }

    [Fact(DisplayName = "A retire racing a fire's completion never deadlocks, and the retired schedules end with no cursor either way")]
    public async Task Retire_and_completion_race_without_deadlock()
    {
        DeadlockRetryOff.SkipOnSqlite(Services);
        var ct = TestContext.Current.CancellationToken;
        for (var round = 0; round < Rounds; round++)
        {
            var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
            var jobName = Unique($"race-retire-{round}");
            var defId = await CreateDefinitionAsync(jobName, ct);
            await RegisterAsync(defId, jobName, cursor, [Slot("a", cursor), Slot("b", cursor)], JobStatusCode.Ready, ct);
            var slotId = await SlotIdAsync(jobName, ct);
            var read = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
            var workerId = await LeaseInFlightAsync(slotId, ct);
            var definition = await Operations.Definitions.GetAsync(TestNamespace, jobName, ct);

            var plannedNext = cursor.AddMinutes(5);
            var completion = CompleteAsync(
                slotId,
                workerId,
                [.. read.Select(s => new ScheduleAdvance(s.ScheduleName, plannedNext, s.Version))],
                plannedNext,
                ct
            );
            var retire = Operations.Definitions.RetireAsync(TestNamespace, jobName, definition!.Version, ct: ct).AsTask();
            await Task.WhenAll(completion, retire);

            Assert.Equal(CompleteExecutionsAction.Completed, (await completion).Action);
            Assert.Equal(ControlAction.Applied, (await retire).Action);
            Assert.All(await ScheduleTestOps.ReadAsync(Db, slotId, ct), s => Assert.Null(s.NextOccurrenceAtUtc));
            // The retire cancels the slot the completion landed first; a completion that lands second parks it.
            Assert.Contains((await ReadJobAsync(slotId, ct)).Status, new[] { JobStatusCode.Cancelled, JobStatusCode.Paused });
        }
    }

    [Fact(DisplayName = "The PostgreSQL registration locks its slots' jobs_state rows in key order, whatever order it is given them in")]
    public async Task The_postgres_registration_locks_slot_rows_in_key_order()
    {
        Assert.SkipUnless(
            Db.Provider == DbProvider.Postgres,
            "The slot-row order is pinned by an ORDER BY in the PostgreSQL registration."
        );
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var lowName = Unique("order-low");
        var highName = Unique("order-high");
        var lowDef = await CreateDefinitionAsync(lowName, ct);
        await RegisterAsync(lowDef, lowName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var highDef = await CreateDefinitionAsync(highName, ct);
        await RegisterAsync(highDef, highName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var lowSlot = await SlotIdAsync(lowName, ct);
        var highSlot = await SlotIdAsync(highName, ct);
        Assert.True(lowSlot < highSlot);

        // A completion settling a child holds the lower slot's row, as it can without the schedule lock.
        await using var gateConnection = await Db.OpenConnectionAsync(ct);
        await using var gate = await gateConnection.BeginTransactionAsync(ct);
        await LockSlotRowAsync(gateConnection, gate, lowSlot, nowait: false, ct);

        var moved = cursor.AddHours(1);
        var registration = ScheduleTestOps.RegisterAsync(
            Services,
            [Definition(highDef, highName, moved, [Slot("only", moved)]), Definition(lowDef, lowName, moved, [Slot("only", moved)])],
            ct
        );
        await Task.Delay(Blocked, ct);
        Assert.False(registration.IsCompleted, "the registration did not wait for the held slot row");

        // Waiting on the lower row, the registration has not locked the higher one.
        await LockSlotRowAsync(gateConnection, gate, highSlot, nowait: true, ct);
        await gate.RollbackAsync(ct);
        await registration.WaitAsync(SpecWaits.Gate, ct);
        Assert.Equal(moved, Assert.Single(await ScheduleTestOps.ReadAsync(Db, highSlot, ct)).NextOccurrenceAtUtc);
    }

    /// <summary>A slot whose one schedule a later declaration orphaned, left holding a cursor written past the registration.</summary>
    private async Task<long> OrphanWithStaleCursorAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        await RegisterAsync(defId, jobName, null, [], JobStatusCode.Paused, ct);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.schedules_state SET next_occurrence_at_utc = @p_next WHERE job_id = @p_id AND schedule_name = @p_name",
            ct,
            ("@p_next", DbValueCoercion.Coerce(cursor, typeof(DateTime), Db.Provider)),
            ("@p_id", slotId),
            ("@p_name", "only")
        );
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(ScheduleStatusCode.Orphaned, schedule.Status);
        Assert.Equal(cursor, schedule.NextOccurrenceAtUtc);
        return slotId;
    }

    /// <summary>A registered one-schedule slot leased to a worker with its attempt in flight, and its schedule as planned.</summary>
    private async Task<(long SlotId, int WorkerId, ScheduleRow Read)> InFlightSlotAsync(
        string jobName,
        DateTime cursor,
        CancellationToken ct
    )
    {
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var read = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        return (slotId, await LeaseInFlightAsync(slotId, ct), read);
    }

    private async Task RegisterOtherAsync(string jobName, DateTime cursor, CancellationToken ct) =>
        await RegisterAsync(await CreateDefinitionAsync(jobName, ct), jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);

    /// <summary>
    /// Updates one schedule's row in <paramref name="table"/> by key on the gate's open transaction, so the row lock
    /// stays held until the gate ends. <paramref name="set"/> may name the instant as <c>@p_at</c>.
    /// </summary>
    private async Task GateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string set,
        long jobId,
        DateTime? at,
        CancellationToken ct
    )
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"UPDATE {Db.Schema}.{table} SET {set} WHERE job_id = @p_id AND schedule_name = @p_name";
        AddParameter(cmd, "@p_id", jobId);
        AddParameter(cmd, "@p_name", "only");
        if (at is { } instant)
        {
            AddParameter(cmd, "@p_at", instant);
        }
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
    }

    /// <summary>Locks one slot's jobs_state row on the gate's open transaction; with <paramref name="nowait"/> a held row fails at once.</summary>
    private async Task LockSlotRowAsync(DbConnection connection, DbTransaction transaction, long jobId, bool nowait, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT job_id FROM {Db.Schema}.jobs_state WHERE job_id = @p_id FOR UPDATE{(nowait ? " NOWAIT" : "")}";
        AddParameter(cmd, "@p_id", jobId);
        Assert.Equal(jobId, Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

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
        DateTime plannedNextRun,
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
                    JobNextRunAtUtc = plannedNextRun,
                },
                ct
            );

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private Task RegisterAsync(
        int defId,
        string jobName,
        DateTime? slotMin,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus,
        CancellationToken ct
    ) => ScheduleTestOps.RegisterAsync(Services, [Definition(defId, jobName, slotMin, schedules, slotStatus)], ct);

    private DefinitionSchedules Definition(
        int defId,
        string jobName,
        DateTime? slotMin,
        IReadOnlyList<SlotSchedule> schedules,
        JobStatusCode slotStatus = JobStatusCode.Ready
    ) =>
        new(
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

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = (await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct))?.JobId;
        Assert.NotNull(id);
        return id!.Value;
    }

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
}
