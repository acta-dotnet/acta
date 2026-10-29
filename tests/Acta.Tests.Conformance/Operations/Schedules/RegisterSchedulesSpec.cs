using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// Startup re-registration against a slot another host is already executing: every host re-registers
/// every declared slot when it starts, so the upsert has to re-assert the declaration without
/// disturbing a row that carries a live execution lease.
/// </summary>
[ConformanceSpec(
    "schedule.slot-re-registration",
    "A second host starting does not disturb a slot the first host is executing",
    Area = "Scheduling",
    Contract = "Registration never moves a slot, writes a cursor only on a changed declaration, retimes on a timing change, and never adopts an ordinary job."
)]
public sealed class RegisterSchedulesSpec : ActaRuntimeTestBase<ConformanceFixture, TestJobs.TestJobsManifest>
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
        await RegisterAsync(defId, jobName, [Slot("only", firstCursor)], ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await LeaseInFlightAsync(Db, slotId, DateTime.UtcNow.AddMinutes(5), ct);

        // The second host's startup reconcile: same definition, a later declared cursor.
        await RegisterAsync(defId, jobName, [Slot("only", secondCursor)], ct);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Executing, slot.Status);

        // The lease has to survive: the heartbeat treats its returned id set as authoritative and
        // cancels any running attempt missing from it, so clearing the lease here strands the body.
        var runtime = Assert.Single(await Db.From<JobStateEntity>().Where(r => r.Id == slotId).ToListAsync(ct));
        Assert.NotNull(runtime.LeasedByWorkerId);
        Assert.Equal(JobStatusCode.Executing, runtime.Status);

        // The declaration still lands, on the row that carries it: admission reads the cursor from the schedule,
        // so skipping the slot does not lose the new cadence.
        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct), s => s.ScheduleName == "only");
        Assert.Equal(secondCursor, schedule.NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "Re-registering an Idle slot leaves it Idle and lands the declared cursor on its schedule")]
    public async Task Re_registering_an_idle_slot_lands_the_declaration_on_the_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-idle-{Guid.NewGuid():N}";
        var firstCursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var secondCursor = FloorSeconds(DateTime.UtcNow.AddHours(9));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("only", firstCursor)], ct);

        var slotId = await SlotIdAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("only", secondCursor)], ct);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        Assert.Equal(secondCursor, Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "Re-registering an unchanged declaration writes nothing: no version moves on the slot or its schedule")]
    public async Task Re_registering_an_unchanged_declaration_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-same-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("only", cursor)], ct);
        var slotId = await SlotIdAsync(jobName, ct);
        var slotBefore = Assert.Single(await Db.From<JobStateEntity>().Where(r => r.Id == slotId).ToListAsync(ct));
        var scheduleBefore = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

        // The restart of an unchanged build: same expression, same cursor.
        await RegisterAsync(defId, jobName, [Slot("only", cursor)], ct);

        var slotAfter = Assert.Single(await Db.From<JobStateEntity>().Where(r => r.Id == slotId).ToListAsync(ct));
        var scheduleAfter = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(slotBefore.Version, slotAfter.Version);
        Assert.Equal(slotBefore.ModifiedAtUtc, slotAfter.ModifiedAtUtc);
        Assert.Equal(scheduleBefore.Version, scheduleAfter.Version);
        Assert.Equal(scheduleBefore.Config.ModifiedAtUtc, scheduleAfter.Config.ModifiedAtUtc);
        Assert.Equal(scheduleBefore.Cursor.ModifiedAtUtc, scheduleAfter.Cursor.ModifiedAtUtc);
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
            await RegisterAsync(defId, jobName, [Slot("only", cursor)], ct);
            var slotId = await SlotIdAsync(jobName, ct);
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = NULL WHERE job_id = @p_id",
                ct,
                ("@p_status", (byte)finished),
                ("@p_id", slotId)
            );

            await RegisterAsync(defId, jobName, [Slot("only", cursor)], ct);

            var slot = await ReadJobAsync(slotId, ct);
            Assert.Equal(finished, slot.Status);
            Assert.Null(slot.NextRunAtUtc);
        }
    }

    [Fact(DisplayName = "An ordinary job keyed by the job name and the slot coexist: registration adopts nothing")]
    public async Task Registration_adopts_no_ordinary_job_keyed_by_the_job_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-taken-{Guid.NewGuid():N}";
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var defId = await CreateDefinitionAsync(jobName, ct);

        // An ordinary job keyed by the job name, before any schedule was declared: unrelated to the slot.
        var ordinary = await Jobs.EnqueueAsync(
            new EnqueueJobRequest(TestNamespace, jobName, JobPayload.Json(1), DeduplicationKey: jobName),
            ct
        );
        var before = await ReadJobAsync(ordinary.JobId, ct);

        var slots = await RegisterAsync(defId, jobName, [Slot("only", cursor)], ct);

        Assert.NotEqual(ordinary.JobId, slots[defId]);
        var after = await ReadJobAsync(ordinary.JobId, ct);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.NextRunAtUtc, after.NextRunAtUtc);
        Assert.Equal(before.JobState.Version, after.JobState.Version);
        Assert.Empty(await Db.From<ScheduleEntity>().Where(s => s.JobId == ordinary.JobId).ToListAsync(ct));
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
            await RegisterAsync(defId, jobName, [Slot("only", due)], ct);
            var slotId = await SlotIdAsync(jobName, ct);
            await SetRuntimeAsync(slotId, suspended ? JobStatusCode.Suspended : JobStatusCode.Ready, suspended ? null : wake, ct);

            await PlanAndRegisterAsync(defId, jobName, Cron5, description: null, now, ct);

            var slot = await ReadJobAsync(slotId, ct);
            Assert.Equal(suspended ? JobStatusCode.Suspended : JobStatusCode.Ready, slot.Status);
            Assert.Equal(suspended ? null : wake, slot.NextRunAtUtc);
            var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
            Assert.Equal(due, schedule.NextOccurrenceAtUtc);
        }
    }

    [Fact(DisplayName = "A start leaves an attempt in flight its due cursor, so a reclaim or re-arm of it still sees its occurrence")]
    public async Task A_start_leaves_an_attempt_in_flight_its_due_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var due = now.AddMinutes(-10);
        var jobName = $"reregister-inflight-due-{Guid.NewGuid():N}";
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("only", due)], ct);
        var slotId = await SlotIdAsync(jobName, ct);
        await LeaseInFlightAsync(Db, slotId, DateTime.UtcNow.AddMinutes(5), ct);

        await PlanAndRegisterAsync(defId, jobName, Cron5, description: null, now, ct);

        var schedule = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(due, schedule.NextOccurrenceAtUtc);
    }

    [Fact(
        DisplayName = "A start that changes a schedule's timing reseeds its cursor after now and audits the retime; an edit of its text does not"
    )]
    public async Task A_start_that_changes_the_timing_retimes_the_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var due = now.AddMinutes(-10);
        var jobName = $"reregister-retime-{Guid.NewGuid():N}";
        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, [Slot("only", due)], ct);
        var slotId = await SlotIdAsync(jobName, ct);

        // The old timing's undecided occurrence is discarded: the cursor moves to the new timing's first occurrence
        // after now.
        await PlanAndRegisterAsync(defId, jobName, "*/7 * * * *", description: null, now, ct);

        var retimed = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.True(retimed.NextOccurrenceAtUtc > now, $"cursor {retimed.NextOccurrenceAtUtc:O} must move past now");
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));

        await PlanAndRegisterAsync(defId, jobName, "*/7 * * * *", description: "edited", now, ct);

        var edited = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(retimed.NextOccurrenceAtUtc, edited.NextOccurrenceAtUtc);
        Assert.Equal("edited", edited.Description);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleRetimed, ct));
    }

    [Fact(DisplayName = "Registration clears a cursor a racing fire left on an orphaned schedule or a retired definition's schedule")]
    public async Task Registration_clears_a_stale_cursor_on_an_orphaned_or_retired_schedule()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));

        var orphanJob = $"heal-orphan-{Guid.NewGuid():N}";
        var orphanDef = await CreateDefinitionAsync(orphanJob, ct);
        await RegisterAsync(orphanDef, orphanJob, [Slot("only", cursor)], ct);
        var orphanSlot = await SlotIdAsync(orphanJob, ct);
        await RegisterAsync(orphanDef, orphanJob, [], ct);

        var retiredJob = $"heal-retired-{Guid.NewGuid():N}";
        var retiredDef = await CreateDefinitionAsync(retiredJob, ct);
        await RegisterAsync(retiredDef, retiredJob, [Slot("only", cursor)], ct);
        var retiredSlot = await SlotIdAsync(retiredJob, ct);
        var definition = await Operations.Definitions.GetAsync(TestNamespace, retiredJob, ct);
        Assert.Equal(
            ControlAction.Applied,
            (await Operations.Definitions.RetireAsync(TestNamespace, retiredJob, definition!.Version, ct: ct)).Action
        );

        // The cursor an advance writes when it lands after the orphaning or the retire it raced.
        foreach (var slotId in new[] { orphanSlot, retiredSlot })
        {
            await Db.ExecuteRawAsync(
                "UPDATE {schema}.schedules_state SET next_occurrence_at_utc = @p_next WHERE schedule_id IN (SELECT s.schedule_id FROM {schema}.schedules s WHERE s.job_id = @p_id)",
                ct,
                ("@p_next", DbValueCoercion.Coerce(cursor, typeof(DateTime), Db.Provider)),
                ("@p_id", slotId)
            );
        }

        // The next registration in the namespace, for any definition, clears both.
        var otherJob = $"heal-other-{Guid.NewGuid():N}";
        await RegisterAsync(await CreateDefinitionAsync(otherJob, ct), otherJob, [Slot("only", cursor)], ct);

        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, orphanSlot, ct)).NextOccurrenceAtUtc);
        Assert.Null(Assert.Single(await ScheduleTestOps.ReadAsync(Db, retiredSlot, ct)).NextOccurrenceAtUtc);
    }

    [Fact(DisplayName = "A slot input with a format and no bytes registers and re-registers as a zero-length input, not NULL")]
    public async Task A_slot_input_with_a_format_and_no_bytes_registers_as_a_zero_length_input()
    {
        var ct = TestContext.Current.CancellationToken;
        var cursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var serializer = Services.GetRequiredService<IJobPayloadSerializerRegistry>().Resolve(JobPayloadFormat.Bytes.Id);

        // The default input of a handler that takes ReadOnlyMemory<byte>, and a custom format whose encoding is empty.
        JobPayload[] inputs =
        [
            serializer.Serialize(default(ReadOnlyMemory<byte>)),
            JobPayload.FromBytes(JobPayloadFormat.Custom(200, "empty-custom"), []),
        ];
        foreach (var input in inputs)
        {
            var jobName = $"register-empty-input-{Guid.NewGuid():N}";
            var defId = await CreateDefinitionAsync(jobName, ct);
            var definition = new DefinitionSchedules(
                NamespaceId: TestNamespaceId,
                DefinitionId: defId,
                InputFormatId: input.Format.Id,
                Input: input.Data,
                EventRecording: EventRecordingCode.Audit,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Schedules: [Slot("only", cursor)]
            );

            // The first start inserts the slot and every later start updates it in place.
            await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
            await ScheduleTestOps.RegisterAsync(Services, [definition], ct);

            var slotId = await SlotIdAsync(jobName, ct);
            var slot = await Db.From<JobEntity>().Where(j => j.Id == slotId).SingleOrDefaultAsync(ct);
            Assert.NotNull(slot);
            Assert.Equal(input.Format.Id, slot.InputFormatId);
            Assert.NotNull(slot.Input);
            Assert.Empty(slot.Input);
        }
    }

    /// <summary>Puts the slot in flight with a lease, as a claim on another host would leave it.</summary>
    private static Task LeaseInFlightAsync(IDbSession db, long jobId, DateTime leaseExpiresAtUtc, CancellationToken ct) =>
        db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, leased_by_worker_id = @p_worker, "
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
        return await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
    }

    /// <summary>A worker start's registration of one declared schedule: read stored state, plan, register.</summary>
    private async Task PlanAndRegisterAsync(
        int defId,
        string jobName,
        string expression,
        string? description,
        DateTime nowUtc,
        CancellationToken ct
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
                expression,
                null,
                MisfireStrategyCode.Skip,
                ScheduleExpressionKindCode.Cron,
                description,
                []
            ),
        };
        await RegisterAsync(defId, jobName, ScheduleRegistrationPlan.Plan(declared, storedForDef, nowUtc), ct);
    }

    private Task SetRuntimeAsync(long slotId, JobStatusCode status, DateTime? nextRunAtUtc, CancellationToken ct) =>
        Db.ExecuteRawAsync(
            "UPDATE {schema}.jobs_state SET job_status_code = @p_status, next_run_at_utc = @p_next WHERE job_id = @p_id",
            ct,
            ("@p_status", (byte)status),
            ("@p_next", nextRunAtUtc),
            ("@p_id", slotId)
        );

    private Task<long> SlotIdAsync(string jobName, CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, jobName, ct);

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
