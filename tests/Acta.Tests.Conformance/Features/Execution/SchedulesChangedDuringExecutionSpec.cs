using Acta.Relational.Commands;
using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Execution;

/// <summary>
/// A recurring slot plans its cursor advances before the handler runs and applies them at completion,
/// and a deployment can change the schedules in between. Completion has to honour the schedules as
/// they stand at that moment, not the plan: never advance or reactivate one that was orphaned, and
/// take the slot's next state from whatever survives, including a schedule that was added meanwhile.
/// </summary>
[ConformanceSpec(
    "execution.schedules-changed-during-execution",
    "Completing an in-flight attempt respects schedule changes made while it ran",
    Area = "Execution",
    Contract = "A recurring completion never advances an orphaned or edited schedule, and derives the slot's status and next run from the schedules current at completion.",
    Arrange = "A slot is leased in flight, then its schedules are removed, partly removed, extended by a re-registration, or one of them is edited.",
    Act = "The attempt completes with the advances it planned before the handler ran.",
    Assert = "Orphaned schedules stay orphaned, an edited one keeps its edit, the slot re-arms from the schedules as they stand, and the events and result row match it."
)]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class SchedulesChangedDuringExecutionSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    protected override bool ParkScheduleSlots => false;

    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";
    private const int Attempt = 1;

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(DisplayName = "Removing the last schedule during execution: completion leaves it orphaned and pauses the slot")]
    public async Task Last_schedule_removed_during_execution_pauses_the_slot()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"changed-last-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var only = Assert.Single(await SchedulesAsync(slotId, ct));

        var workerId = await LeaseInFlightAsync(slotId, ct);

        // The deployment: the same definition, declaring no schedules. Re-registration leaves the
        // executing slot row alone and orphans the schedule.
        await RegisterAsync(defId, jobName, null, [], JobStatusCode.Paused, ct);
        Assert.Equal(ScheduleStatusCode.Orphaned, Assert.Single(await SchedulesAsync(slotId, ct)).Status);

        // The attempt finishes with the plan it made before the handler ran: advance the schedule and
        // re-arm at its next cursor. Both are stale now.
        var plannedNext = cursor.AddMinutes(5);
        var result = await CompleteAsync(
            slotId,
            workerId,
            [new ScheduleAdvance(only.Id, plannedNext)],
            JobStatusCode.Ready,
            plannedNext,
            ct
        );

        // The runtime decides whether to wake from the result row, so it must carry the derived state.
        Assert.Equal(CompleteExecutionAction.Completed, result.Action);
        Assert.Equal((byte)JobStatusCode.Paused, result.FinalStatusCode);
        Assert.Null(result.FinalNextRunAtUtc);

        var schedule = Assert.Single(await SchedulesAsync(slotId, ct));
        Assert.Equal(ScheduleStatusCode.Orphaned, schedule.Status);
        Assert.Equal(cursor, schedule.NextRunAtUtc);

        var runtime = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Paused, runtime.Status);
        Assert.Null(runtime.NextRunAtUtc);

        // The audit row says what the runtime row says: paused, not rolled over.
        var events = await Db.From<JobEvent>().Where(e => e.JobId == slotId && e.ExecutionNumber == Attempt).ToListAsync(ct);
        Assert.Contains(events, e => e.EventCode == EventCode.JobPaused);
        var finished = Assert.Single(events, e => e.EventCode == EventCode.JobExecutionFinished);
        Assert.Equal(JobStatusCode.Paused, finished.ToStatus);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.JobRecurringRolledOver);
    }

    [Fact(DisplayName = "Removing the earliest of several schedules: completion re-arms at the earliest survivor")]
    public async Task Earliest_schedule_removed_during_execution_re_arms_at_the_survivor()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"changed-earliest-{Guid.NewGuid():N}";
        var early = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var late = FloorSeconds(DateTime.UtcNow.AddHours(9));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, early, [Slot("early", early), Slot("late", late)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var schedules = await SchedulesAsync(slotId, ct);
        var earlyRow = Assert.Single(schedules, s => s.NextRunAtUtc == early);
        var lateRow = Assert.Single(schedules, s => s.NextRunAtUtc == late);

        var workerId = await LeaseInFlightAsync(slotId, ct);

        // The deployment drops the early schedule and keeps the late one.
        await RegisterAsync(defId, jobName, late, [Slot("late", late)], JobStatusCode.Ready, ct);

        // The plan advanced both and re-armed at the early schedule's next cursor, still the earliest.
        var earlyNext = early.AddMinutes(5);
        var lateNext = late.AddMinutes(5);
        var result = await CompleteAsync(
            slotId,
            workerId,
            [new ScheduleAdvance(earlyRow.Id, earlyNext), new ScheduleAdvance(lateRow.Id, lateNext)],
            JobStatusCode.Ready,
            earlyNext,
            ct
        );
        Assert.Equal((byte)JobStatusCode.Ready, result.FinalStatusCode);
        Assert.Equal(lateNext, result.FinalNextRunAtUtc);

        var after = await SchedulesAsync(slotId, ct);
        var earlyAfter = Assert.Single(after, s => s.Id == earlyRow.Id);
        Assert.Equal(ScheduleStatusCode.Orphaned, earlyAfter.Status);
        Assert.Equal(early, earlyAfter.NextRunAtUtc);
        var lateAfter = Assert.Single(after, s => s.Id == lateRow.Id);
        Assert.Equal(ScheduleStatusCode.Active, lateAfter.Status);
        Assert.Equal(lateNext, lateAfter.NextRunAtUtc);

        var runtime = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, runtime.Status);
        Assert.Equal(lateNext, runtime.NextRunAtUtc);
        await AssertRolledOverAsync(slotId, ct);
    }

    [Fact(DisplayName = "Adding a schedule during execution: its cursor takes part in the slot's next run")]
    public async Task Schedule_added_during_execution_takes_part_in_the_next_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"changed-added-{Guid.NewGuid():N}";
        var existing = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var added = FloorSeconds(DateTime.UtcNow.AddHours(3));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, existing, [Slot("existing", existing)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var existingRow = Assert.Single(await SchedulesAsync(slotId, ct));

        var workerId = await LeaseInFlightAsync(slotId, ct);

        // The deployment adds a schedule due before the existing one will next fire.
        await RegisterAsync(defId, jobName, added, [Slot("existing", existing), Slot("added", added)], JobStatusCode.Ready, ct);

        var existingNext = existing.AddMinutes(5);
        var result = await CompleteAsync(
            slotId,
            workerId,
            [new ScheduleAdvance(existingRow.Id, existingNext, existingRow.Version)],
            JobStatusCode.Ready,
            existingNext,
            ct
        );
        Assert.Equal((byte)JobStatusCode.Ready, result.FinalStatusCode);
        Assert.Equal(added, result.FinalNextRunAtUtc);

        var runtime = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, runtime.Status);
        Assert.Equal(added, runtime.NextRunAtUtc);
        await AssertRolledOverAsync(slotId, ct);
    }

    [Fact(DisplayName = "Editing a schedule during execution: completion keeps the edit and refuses its stale advance")]
    public async Task Schedule_edited_during_execution_keeps_the_edit()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"changed-edited-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var edited = FloorSeconds(DateTime.UtcNow.AddHours(2));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var read = Assert.Single(await SchedulesAsync(slotId, ct));

        var workerId = await LeaseInFlightAsync(slotId, ct);

        // An operator moves the cursor while the attempt runs; every edit verb bumps the version.
        await EditCursorAsync(read.Id, edited, ct);

        // The plan was made against the version read before the edit.
        var plannedNext = cursor.AddMinutes(5);
        var result = await CompleteAsync(
            slotId,
            workerId,
            [new ScheduleAdvance(read.Id, plannedNext, read.Version)],
            JobStatusCode.Ready,
            plannedNext,
            ct
        );

        var after = Assert.Single(await SchedulesAsync(slotId, ct));
        Assert.Equal(edited, after.NextRunAtUtc);
        Assert.Equal(read.Version + 1, after.Version);
        Assert.Equal(ScheduleStatusCode.Active, after.Status);

        // The slot re-arms from the schedule as it stands, and the result row says so.
        var runtime = await RuntimeAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, runtime.Status);
        Assert.Equal(edited, runtime.NextRunAtUtc);
        Assert.Equal(edited, result.FinalNextRunAtUtc);
    }

    /// <summary>An operator edit as the verbs leave it: a new cursor, a new version, a new modified stamp.</summary>
    private Task EditCursorAsync(long scheduleId, DateTime nextRunAtUtc, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.schedules SET next_run_at_utc = @p_next, modified_at_utc = @p_next, version = version + 1 WHERE id = @p_id",
            ct,
            ("@p_next", DbValueCoercion.Coerce(nextRunAtUtc, typeof(DateTime), Db.Provider)),
            ("@p_id", scheduleId)
        );

    /// <summary>The audit trail of a re-armed slot: one finished and one rolled-over event, both saying Ready.</summary>
    private async Task AssertRolledOverAsync(long slotId, CancellationToken ct)
    {
        var events = await Db.From<JobEvent>().Where(e => e.JobId == slotId && e.ExecutionNumber == Attempt).ToListAsync(ct);
        Assert.Equal(JobStatusCode.Ready, Assert.Single(events, e => e.EventCode == EventCode.JobExecutionFinished).ToStatus);
        Assert.Equal(JobStatusCode.Ready, Assert.Single(events, e => e.EventCode == EventCode.JobRecurringRolledOver).ToStatus);
        Assert.DoesNotContain(events, e => e.EventCode == EventCode.JobPaused);
    }

    private async Task<int> LeaseInFlightAsync(long jobId, CancellationToken ct)
    {
        // A real worker row, so the lease names an owner that exists and completion's owner check holds.
        var worker = await Db.From<JobWorker>().Where(w => w.NamespaceId == TestNamespaceId).FirstOrDefaultAsync(ct);
        Assert.NotNull(worker);
        await Db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, execution_number = @p_attempt, leased_by_worker_id = @p_worker, "
                + "lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_attempt", Attempt),
            ("@p_worker", worker!.Id),
            ("@p_expires", DateTime.UtcNow.AddMinutes(5)),
            ("@p_id", jobId)
        );
        return worker.Id;
    }

    private Task<CompleteExecutionResult> CompleteAsync(
        long jobId,
        int workerId,
        IReadOnlyList<ScheduleAdvance> advances,
        JobStatusCode plannedStatus,
        DateTime? plannedNextRun,
        CancellationToken ct
    ) =>
        Services
            .GetRequiredService<IExecutionStore>()
            .CompleteExecutionAsync(
                new CompleteExecutionRequest(
                    JobId: jobId,
                    WorkerId: workerId,
                    ExpectedExecutionNumber: Attempt,
                    Outcome: ExecutionOutcome.Succeeded,
                    ResultFormatId: 0,
                    Result: ReadOnlyMemory<byte>.Empty,
                    ScheduleAdvances: advances,
                    FinalStatus: plannedStatus,
                    JobNextRunAtUtc: plannedNextRun,
                    FailureCount: 0
                ),
                ct
            );

    private Task<IReadOnlyList<JobSchedule>> SchedulesAsync(long jobId, CancellationToken ct) =>
        Db.From<JobSchedule>().Where(s => s.JobId == jobId).ToListAsync(ct);

    private async Task<JobRuntime> RuntimeAsync(long jobId, CancellationToken ct) =>
        Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == jobId).ToListAsync(ct));

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private async Task RegisterAsync(
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
            AuditLevel: JobAuditLevelCode.Audit,
            SlotStatus: slotStatus,
            SlotMinNextRunAtUtc: slotMin,
            Schedules: schedules
        );
        await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = await Jobs.GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

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
            InvocationKind: default,
            RequiresJobContextParameter: false,
            RequiresCancellationToken: false,
            Priority: default,
            MaxAttempts: 1,
            AuditLevel: default,
            AlertProfile: default,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
