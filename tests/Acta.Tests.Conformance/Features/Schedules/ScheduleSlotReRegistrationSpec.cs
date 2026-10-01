using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Features.Schedules;

/// <summary>
/// Startup re-registration against a slot another host is already executing: every host re-registers
/// every declared slot when it starts, so the upsert has to re-assert the declaration without
/// disturbing a row that carries a live execution lease.
/// </summary>
[ConformanceSpec(
    "schedule.slot-re-registration",
    "A second host starting does not disturb a slot the first host is executing",
    Area = "Scheduling",
    Contract = "Registration writes an idle slot only on a changed declaration, leaves one in flight, finished, or parked mid-occurrence, and never adopts an ordinary job.",
    Arrange = "A recurring slot is registered, then put in flight with a worker lease as if another host had claimed it.",
    Act = "The same definition is registered again, as a second host does on startup, with the declaration changed or identical.",
    Assert = "In-flight and parked slots keep their status, lease or wake, and cursor, while an idle slot takes a changed declaration and ignores an identical one."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
public abstract class ScheduleSlotReRegistrationSpec<TFixture> : ActaRuntimeTestBase<TFixture, TestJobs.TestJobsManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private int TestNamespaceId => Runtime.RegisteredNamespaceIds[TestNamespace];

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    [Fact(DisplayName = "Re-registering a slot that is executing leaves its status, lease, and cursor to the running execution")]
    public async Task Re_registering_an_in_flight_slot_leaves_it_to_the_running_execution()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-inflight-{Guid.NewGuid():N}";
        var firstCursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var secondCursor = FloorSeconds(DateTime.UtcNow.AddHours(9));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, firstCursor, [Slot("only", firstCursor)], JobStatusCode.Ready, ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await LeaseInFlightAsync(Db, slotId, DateTime.UtcNow.AddMinutes(5), ct);

        // The second host's startup reconcile: same definition, a later declared cursor.
        await RegisterAsync(defId, jobName, secondCursor, [Slot("only", secondCursor)], JobStatusCode.Ready, ct);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Executing, slot.Status);

        // The lease has to survive: the heartbeat treats its returned id set as authoritative and
        // cancels any running attempt missing from it, so clearing the lease here strands the body.
        var runtime = Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == slotId).ToListAsync(ct));
        Assert.NotNull(runtime.LeasedByWorkerId);
        Assert.Equal(JobStatusCode.Executing, runtime.Status);

        // The declaration still lands, on the row that carries it: the recurring completion reads its
        // next run back from schedules, so skipping the slot does not lose the new cadence.
        var schedule = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId && s.Name == "only").ToListAsync(ct));
        Assert.Equal(secondCursor, schedule.NextRunAtUtc);
    }

    [Fact(DisplayName = "Re-registering an idle slot re-asserts the declared cursor and status")]
    public async Task Re_registering_an_idle_slot_re_asserts_the_declaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-idle-{Guid.NewGuid():N}";
        var firstCursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var secondCursor = FloorSeconds(DateTime.UtcNow.AddHours(9));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, firstCursor, [Slot("only", firstCursor)], JobStatusCode.Ready, ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await RegisterAsync(defId, jobName, secondCursor, [Slot("only", secondCursor)], JobStatusCode.Ready, ct);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(secondCursor, slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "Re-registering an unchanged declaration writes nothing: no version moves on the slot or its schedule")]
    public async Task Re_registering_an_unchanged_declaration_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-same-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var slotBefore = Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == slotId).ToListAsync(ct));
        var scheduleBefore = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct));

        // The restart of an unchanged build: same expression, same cursor, same status.
        await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);

        var slotAfter = Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == slotId).ToListAsync(ct));
        var scheduleAfter = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct));
        Assert.Equal(slotBefore.Version, slotAfter.Version);
        Assert.Equal(slotBefore.ModifiedAtUtc, slotAfter.ModifiedAtUtc);
        Assert.Equal(scheduleBefore.Version, scheduleAfter.Version);
        Assert.Equal(scheduleBefore.ModifiedAtUtc, scheduleAfter.ModifiedAtUtc);
    }

    [Fact(DisplayName = "Re-registering a finished slot leaves it finished: only an operator's restart brings it back")]
    public async Task Re_registering_a_finished_slot_leaves_it_finished()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        foreach (var finished in new[] { JobStatusCode.Succeeded, JobStatusCode.Failed, JobStatusCode.Cancelled })
        {
            var jobName = $"reregister-finished-{Guid.NewGuid():N}";
            var defId = await CreateDefinitionAsync(jobName, ct);
            await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);
            var slotId = await SlotIdAsync(jobName, ct);
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = NULL WHERE job_id = @p_id",
                ct,
                ("@p_status", (byte)finished),
                ("@p_id", slotId)
            );

            await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);

            var slot = await ReadJobAsync(slotId, ct);
            Assert.Equal(finished, slot.Status);
            Assert.Null(slot.NextRunAtUtc);
        }
    }

    [Fact(DisplayName = "Registration leaves an ordinary job that holds the slot's key untouched and returns no slot for it")]
    public async Task Registration_leaves_an_ordinary_job_holding_the_slot_key_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-taken-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var defId = await CreateDefinitionAsync(jobName, ct);

        // An ordinary job that took the key a slot uses, its job name, before any schedule was declared.
        var ordinary = await Jobs.EnqueueAsync(
            new JobEnqueueRequest(TestNamespace, jobName, JobPayload.Json(1), DeduplicationKey: jobName),
            ct
        );
        var before = await ReadJobAsync(ordinary.JobId, ct);

        var slots = await RegisterAsync(defId, jobName, cursor, [Slot("only", cursor)], JobStatusCode.Ready, ct);

        Assert.DoesNotContain(defId, slots.Keys);
        var after = await ReadJobAsync(ordinary.JobId, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);
        Assert.Equal(before.Runtime.Version, after.Runtime.Version);
        Assert.Empty(await Db.From<JobSchedule>().Where(s => s.JobId == ordinary.JobId).ToListAsync(ct));
    }

    [Fact(
        DisplayName = "A start leaves a slot parked part-way through an occurrence where it waits: Suspended, or Ready at its wake instant"
    )]
    public async Task A_start_leaves_a_parked_slot_where_it_waits()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var due = now.AddMinutes(-10);
        var wake = now.AddMinutes(10);

        foreach (var suspended in new[] { false, true })
        {
            // The occurrence due ten minutes ago began, then re-armed at a wake instant or parked on a wait;
            // a re-arm never advances its cursor.
            var jobName = $"reregister-parked-{Guid.NewGuid():N}";
            var defId = await CreateDefinitionAsync(jobName, ct);
            await RegisterAsync(defId, jobName, wake, [Slot("only", due)], JobStatusCode.Ready, ct);
            var slotId = await SlotIdAsync(jobName, ct);
            if (suspended)
            {
                await Db.ExecuteRawAsync(
                    "UPDATE {schema}.runtimes SET status_code = @p_status, next_run_at_utc = NULL WHERE job_id = @p_id",
                    ct,
                    ("@p_status", (byte)JobStatusCode.Suspended),
                    ("@p_id", slotId)
                );
            }

            await ReconcileAndRegisterAsync(defId, jobName, MisfireStrategyCode.Skip, description: null, now, ct);

            var slot = await ReadJobAsync(slotId, ct);
            Assert.Equal(suspended ? JobStatusCode.Suspended : JobStatusCode.Ready, slot.Status);
            Assert.Equal(suspended ? null : wake, slot.NextRunAtUtc);
            var schedule = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct));
            Assert.Equal(due, schedule.NextRunAtUtc);
        }
    }

    [Fact(DisplayName = "A start that edits a catch-up schedule whose occurrence is in flight moves its cursor past that occurrence")]
    public async Task A_start_that_edits_an_in_flight_catch_up_schedule_moves_past_the_occurrence()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var due = now.AddMinutes(-10);
        var jobName = $"reregister-catchup-{Guid.NewGuid():N}";
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, now.AddHours(6), [Slot("only", due, MisfireStrategyCode.CatchUpOnce)], JobStatusCode.Ready, ct);
        var slotId = await SlotIdAsync(jobName, ct);
        await LeaseInFlightAsync(Db, slotId, DateTime.UtcNow.AddMinutes(5), ct);

        // The edit bumps the schedule's version, so the running attempt's own advance is refused and the
        // slot re-arms from this cursor: left at the running occurrence, that occurrence would run again.
        await ReconcileAndRegisterAsync(defId, jobName, MisfireStrategyCode.CatchUpOnce, description: "edited", now, ct);

        var schedule = Assert.Single(await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct));
        Assert.True(schedule.NextRunAtUtc > now, $"cursor {schedule.NextRunAtUtc:O} must move past the running occurrence");
        Assert.Equal("edited", schedule.Description);
    }

    /// <summary>Puts the slot in flight with a lease, as a claim on another host would leave it.</summary>
    private static Task LeaseInFlightAsync(IDbSession db, long jobId, DateTime leaseExpiresAtUtc, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.runtimes SET status_code = @p_status, leased_by_worker_id = @p_worker, "
                + "lease_expires_at_utc = @p_expires WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)JobStatusCode.Executing),
            ("@p_worker", 1),
            ("@p_expires", leaseExpiresAtUtc),
            ("@p_id", jobId)
        );

    private async Task<int> CreateDefinitionAsync(string jobName, CancellationToken ct)
    {
        var map = await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct);
        return map[jobName];
    }

    private async Task<IReadOnlyDictionary<int, long>> RegisterAsync(
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
        return await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    /// <summary>A worker start's reconcile of one declared schedule: read stored state, reconcile, register.</summary>
    private async Task ReconcileAndRegisterAsync(
        int defId,
        string jobName,
        MisfireStrategyCode misfire,
        string? description,
        DateTime nowUtc,
        CancellationToken ct
    )
    {
        var stored = await Services.GetRequiredService<IScheduleStore>().GetScheduleStateAsync(TestNamespaceId, ct);
        var storedForDef = stored.Where(s => s.DefinitionId == defId).ToDictionary(s => s.ScheduleName, s => s, StringComparer.Ordinal);
        var declared = new[]
        {
            new ScheduleDescriptor(jobName, "only", Cron5, null, misfire, ScheduleExpressionKindCode.Cron, description, []),
        };
        var (slotSchedules, slotStatus, slotNextRun) = ScheduleWalker.Reconcile(declared, storedForDef, nowUtc);
        await RegisterAsync(defId, jobName, slotNextRun, slotSchedules, slotStatus, ct);
    }

    private async Task<long> SlotIdAsync(string jobName, CancellationToken ct)
    {
        var id = await Services.GetRequiredService<IJobs>().GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private static SlotSchedule Slot(string name, DateTime cursor, MisfireStrategyCode misfire = MisfireStrategyCode.Skip) =>
        new(name, Cron5, null, misfire, ScheduleExpressionKindCode.Cron, null, cursor);

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
