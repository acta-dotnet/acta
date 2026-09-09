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
    Contract = "Re-registration re-asserts the declaration on idle slots and skips slots in flight, leaving their status, lease, and cursor to the execution that owns them.",
    Arrange = "A recurring slot is registered, then put in flight with a worker lease as if another host had claimed it.",
    Act = "The same definition is registered again, as a second host does on startup.",
    Assert = "The in-flight slot keeps its status, lease, and cursor, while the schedule row still takes the new declaration."
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

    [Fact(DisplayName = "Re-registering a slot whose lease has expired re-arms it instead of skipping it")]
    public async Task Re_registering_a_stranded_slot_re_arms_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var jobName = $"reregister-stranded-{Guid.NewGuid():N}";
        var firstCursor = FloorSeconds(DateTime.UtcNow.AddHours(6));
        var secondCursor = FloorSeconds(DateTime.UtcNow.AddHours(9));

        var defId = await CreateDefinitionAsync(jobName, ct);
        await RegisterAsync(defId, jobName, firstCursor, [Slot("only", firstCursor)], JobStatusCode.Ready, ct);

        var slotId = await SlotIdAsync(jobName, ct);

        // A worker claimed the slot and was killed. The row still reads Executing, but the lease has
        // lapsed and nothing is running behind it. sys.recovery is itself a slot and the only caller of
        // the reclaim sweep, so when it is the stranded one no sweep can free it and this is the only
        // path back.
        await LeaseInFlightAsync(Db, slotId, DateTime.UtcNow.AddMinutes(-5), ct);

        await RegisterAsync(defId, jobName, secondCursor, [Slot("only", secondCursor)], JobStatusCode.Ready, ct);

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);

        // The lease has to be cleared with the status: ck_runtimes_status_lease admits a lease holder
        // only in Dispatched or Executing, so re-arming without clearing it would fail the write.
        var runtime = Assert.Single(await Db.From<JobRuntime>().Where(r => r.Id == slotId).ToListAsync(ct));
        Assert.Null(runtime.LeasedByWorkerId);
        Assert.Equal(secondCursor, runtime.NextRunAtUtc);
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
        var id = await Services.GetRequiredService<IJobs>().GetJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, jobName), ct);
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
