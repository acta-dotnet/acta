using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// The atomic apply of an admission plan: a group applies whole against the versions it was planned at, an admitting
/// group also needs its slot Idle, and a stale group applies nothing so the admitter replans it. A suspension takes the
/// namespace's schedule lock exclusive, so a plan read before it either lands first or applies nothing.
/// </summary>
[ConformanceSpec(
    "schedule.admit-firings",
    "An admission plan applies whole per slot, or not at all when stale",
    Area = "Scheduling",
    Contract = "A planned group applies whole at the versions it read, making the slot Ready with its envelope, and a moved group or a suspended namespace's applies nothing."
)]
public sealed class AdmitScheduleFiringsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private IJobs Jobs => Services.GetRequiredService<IJobs>();

    private INamespaces Namespaces => Services.GetRequiredService<IActaOperations>().Namespaces;

    [Fact(DisplayName = "An applied admitting group makes the slot Ready with the envelope, advances the member, and audits the admission")]
    public async Task An_applied_group_admits_the_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("admit");
        var slotId = await RegisterAsync(jobName, now.AddMinutes(-1), ct);
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

        var plan = await PlanAsync(now, ct);
        var applied = Assert.Single((await Admit(plan, ct)).Groups);

        Assert.Equal(0, applied.Ordinal);
        Assert.True(applied.Applied);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.NotNull(slot.NextRunAtUtc);
        var firing = Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).ToListAsync(ct)
        );
        Assert.Equal(CheckpointStatusCode.Pending, firing.Status);
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.True(after.NextOccurrenceAtUtc > now, "the admitted occurrence was not decided");
        Assert.Equal(before.Cursor.Version + 1, after.Cursor.Version);
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
    }

    [Fact(DisplayName = "A group planned against a version another writer moved applies nothing")]
    public async Task A_stale_group_applies_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("stale");
        var slotId = await RegisterAsync(jobName, now.AddMinutes(-1), ct);
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));

        // Planned Idle at one version; an operator holds the slot before the plan lands.
        var plan = await PlanAsync(now, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);

        var stale = Assert.Single((await Admit(plan, ct)).Groups);

        Assert.False(stale.Applied);
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId).ToListAsync(ct));
        var after = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(before.NextOccurrenceAtUtc, after.NextOccurrenceAtUtc);
        Assert.Equal(before.Cursor.Version, after.Cursor.Version);
        Assert.Equal(0, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
    }

    [Fact(DisplayName = "A cursor-only group parks a member on a busy slot without touching the slot")]
    public async Task A_cursor_only_group_parks_the_member()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("busy");
        var slotId = await RegisterAsync(jobName, now.AddMinutes(-1), ct);
        await ChaosSpecHelpers.SetReadyAsync(Db, slotId, ct);
        var slotBefore = await ReadJobAsync(slotId, ct);

        var plan = await PlanAsync(now, ct);
        var group = Assert.Single(plan.Groups);
        Assert.False(group.Admit);

        Assert.True(Assert.Single((await Admit(plan, ct)).Groups).Applied);
        var member = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.Equal(EvaluationStatusCode.WaitingForCapacity, member.EvaluationStatus);
        Assert.Null(member.NextEvaluationAtUtc);
        var slotAfter = await ReadJobAsync(slotId, ct);
        Assert.Equal(slotBefore.Status, slotAfter.Status);
        Assert.Equal(slotBefore.JobState.Version, slotAfter.JobState.Version);
    }

    [Fact(DisplayName = "A slot group applies nothing when a member it left unchanged had its declaration changed after the plan read it")]
    public async Task A_member_declaration_changed_after_the_plan_fails_its_group()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("member");
        var due = Schedule("due", now.AddMinutes(-1));
        var later = Schedule("later", now.AddHours(6));
        var slotId = (await RegisterAsync(jobName, ScheduledDeliveryCode.Permanent, [due, later], ct)).SlotId!.Value;
        var before = await ScheduleTestOps.ReadAsync(Db, slotId, ct);

        var plan = await PlanAsync(now, ct);
        Assert.True(Assert.Single(plan.Groups).Admit);
        Assert.Contains(plan.Rows, r => r.ScheduleName == "later" && r.CheckOnly);

        // The misfire policy sits outside the timing fingerprint, so the change moves the declaration and no cursor.
        await RegisterAsync(
            jobName,
            ScheduledDeliveryCode.Permanent,
            [due, later with { MisfireStrategy = MisfireStrategyCode.CatchUpOnce }],
            ct
        );
        var registered = await ScheduleTestOps.ReadAsync(Db, slotId, ct);
        Assert.Equal(before.Select(s => s.Cursor.Version), registered.Select(s => s.Cursor.Version));

        Assert.False(Assert.Single((await Admit(plan, ct)).Groups).Applied);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(before.Select(s => s.Cursor.Version), (await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Select(s => s.Cursor.Version));
    }

    [Fact(DisplayName = "A per-occurrence plan read before its capacity was lowered admits nothing, and the outstanding count stays")]
    public async Task A_capacity_lowered_after_the_plan_admits_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var jobName = Unique("capacity");
        var roomy = Schedule("only", now.AddMinutes(-1)) with { MaxOutstanding = 2 };
        var (defId, _) = await RegisterAsync(jobName, ScheduledDeliveryCode.PerOccurrence, [roomy], ct);
        var scheduleId = (await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == defId).SingleOrDefaultAsync(ct))!.Id;

        // One root is outstanding, so the plan sees room for one more.
        Assert.Equal(
            1,
            await Db.From<ScheduleStateEntity>()
                .Where(s => s.Id == scheduleId)
                .UpdateOnlyAsync(() => new ScheduleStateEntity { OutstandingCount = 1 }, ct)
        );
        var plan = await PlanAsync(now, ct);
        Assert.True(Assert.Single(plan.Groups).Admit);
        var state = await Db.From<ScheduleStateEntity>().Where(s => s.Id == scheduleId).SingleOrDefaultAsync(ct);

        await RegisterAsync(jobName, ScheduledDeliveryCode.PerOccurrence, [roomy with { MaxOutstanding = 1 }], ct);
        Assert.Equal(
            state!.Version,
            (await Db.From<ScheduleStateEntity>().Where(s => s.Id == scheduleId).SingleOrDefaultAsync(ct))!.Version
        );

        Assert.False(Assert.Single((await Admit(plan, ct)).Groups).Applied);
        Assert.Equal(1, (await Db.From<ScheduleStateEntity>().Where(s => s.Id == scheduleId).SingleOrDefaultAsync(ct))!.OutstandingCount);
        Assert.Equal(0, await Db.From<JobEntity>().Where(j => j.DefinitionId == defId).CountAsync(ct));
    }

    [Fact(DisplayName = "A slot plan read before a suspension committed admits nothing")]
    public async Task A_slot_plan_across_a_suspension_admits_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var slotId = await RegisterAsync(Unique("held"), now.AddMinutes(-1), ct);
        var before = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        var plan = await PlanAsync(now, ct);
        Assert.True(Assert.Single(plan.Groups).Admit);

        Assert.Equal(ControlAction.Applied, (await Namespaces.SuspendAsync(TestNamespace, ct: ct)).Action);

        Assert.False(Assert.Single((await Admit(plan, ct)).Groups).Applied);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId).ToListAsync(ct));
        Assert.Equal(before.Cursor.Version, Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Cursor.Version);
    }

    [Fact(DisplayName = "A per-occurrence plan read before a suspension committed inserts no root and charges nothing")]
    public async Task A_root_plan_across_a_suspension_admits_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var (defId, _) = await RegisterAsync(
            Unique("held-root"),
            ScheduledDeliveryCode.PerOccurrence,
            [Schedule("only", now.AddMinutes(-1))],
            ct
        );
        var plan = await PlanAsync(now, ct);
        Assert.True(Assert.Single(plan.Groups).Admit);

        Assert.Equal(ControlAction.Applied, (await Namespaces.SuspendAsync(TestNamespace, ct: ct)).Action);

        Assert.False(Assert.Single((await Admit(plan, ct)).Groups).Applied);
        Assert.Equal(0, await Db.From<JobEntity>().Where(j => j.DefinitionId == defId).CountAsync(ct));
        var scheduleId = (await Db.From<ScheduleEntity>().Where(s => s.DefinitionId == defId).SingleOrDefaultAsync(ct))!.Id;
        Assert.Equal(0, (await Db.From<ScheduleStateEntity>().Where(s => s.Id == scheduleId).SingleOrDefaultAsync(ct))!.OutstandingCount);
    }

    [Fact(DisplayName = "An admission waits for a suspension that holds the schedule lock, then admits nothing")]
    public async Task An_admission_waits_for_a_suspension_then_admits_nothing()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        var now = FloorSeconds(DateTime.UtcNow);
        var slotId = await RegisterAsync(Unique("waits"), now.AddMinutes(-1), ct);
        var plan = await PlanAsync(now, ct);

        // A suspension mid-flight: the lock taken exclusive, then the namespace row written, as SuspendNamespace does.
        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var suspension = await connection.BeginTransactionAsync(ct);
        await ScheduleLockGate.HoldAsync(Db, connection, suspension, TestNamespaceId, exclusive: true, ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = suspension;
            cmd.CommandText =
                $"UPDATE {Db.Schema}.namespaces SET namespace_status_code = {(int)NamespaceStatusCode.Suspended} WHERE namespace_id = {TestNamespaceId}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        var pending = Admit(plan, ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        Assert.False(pending.IsCompleted, "the admission did not wait for the suspension's schedule lock");
        await suspension.CommitAsync(ct);

        Assert.False(Assert.Single((await pending).Groups).Applied);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "A suspension waits for an admission that holds the schedule lock, and the admission it waited for stands")]
    public async Task A_suspension_waits_for_an_admission_in_flight()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        var admission = await ScheduleLockGate.OpenAsync(Db, TestNamespaceId, exclusive: false, ct);
        Task<SuspendNamespaceResponse> pending;
        try
        {
            pending = Namespaces.SuspendAsync(TestNamespace, ct: ct).AsTask();
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            Assert.False(pending.IsCompleted, "the suspension did not wait for the admission's schedule lock");
        }
        finally
        {
            await admission.DisposeAsync();
        }

        Assert.Equal(ControlAction.Applied, (await pending).Action);
    }

    [Fact(DisplayName = "A manual firing that read its namespace Active waits for a suspension in flight, then is rejected")]
    public async Task A_manual_firing_behind_a_suspension_is_rejected()
    {
        SkipOnSqlite();
        var ct = TestContext.Current.CancellationToken;
        var jobName = Unique("manual");
        var slotId = await RegisterAsync(jobName, FloorSeconds(DateTime.UtcNow.AddHours(6)), ct);

        await using var connection = await Db.OpenConnectionAsync(ct);
        await using var suspension = await connection.BeginTransactionAsync(ct);
        await ScheduleLockGate.HoldAsync(Db, connection, suspension, TestNamespaceId, exclusive: true, ct);

        // The firing reads its target Active and stops at the lock before the suspension writes the namespace row, which
        // a locking read would otherwise wait on until the suspension commits.
        var lookup = new ScheduleLookup(TestNamespace, jobName, "only");
        var target = await Services.GetRequiredService<GetScheduleTargetHandler>().HandleAsync(new GetScheduleTargetQuery(lookup), ct);
        Assert.Equal(NamespaceStatusCode.Active, target!.NamespaceStatus);
        var schedules = Services.GetRequiredService<IActaOperations>().Schedules;
        var pending = schedules.TriggerAsync(lookup, ct: ct).AsTask();
        var firingSql = Db.Provider == DbProvider.SqlServer ? "@p_schedule_id" : "pg_advisory_xact_lock_shared";
        await LockWaitProbe.WaitAsync(Db, firingSql, LockWait.ApplicationLock, pending, suspension, ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = suspension;
            cmd.CommandText =
                $"UPDATE {Db.Schema}.namespaces SET namespace_status_code = {(int)NamespaceStatusCode.Suspended} WHERE namespace_id = {TestNamespaceId}";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync(ct));
        }

        await suspension.CommitAsync(ct);

        Assert.Equal(ControlAction.Rejected, (await pending).Action);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Empty(await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId).ToListAsync(ct));
    }

    /// <summary>SQLite runs one writer at a time, so no transaction holds the schedule lock beside another.</summary>
    private void SkipOnSqlite() =>
        Assert.SkipWhen(Db.Provider == DbProvider.Sqlite, "SQLite runs one writer at a time, so no second transaction can hold the lock.");

    private async Task<AdmitScheduleFiringsCommand> PlanAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = await Services
            .GetRequiredService<ListDueSchedulesHandler>()
            .HandleAsync(new ListDueSchedulesQuery(TestNamespaceId, nowUtc, 100), ct);
        return ScheduleAdmitter.Plan(TestNamespaceId, due.Schedules, nowUtc, Services.GetRequiredService<CalendarsOperations>());
    }

    private Task<AdmitScheduleFiringsResponse> Admit(AdmitScheduleFiringsCommand plan, CancellationToken ct) =>
        Services.GetRequiredService<AdmitScheduleFiringsHandler>().HandleAsync(plan, ct);

    /// <summary>One definition with one Skip schedule whose cursor sits at <paramref name="cursor"/>; returns its slot.</summary>
    private async Task<long> RegisterAsync(string jobName, DateTime cursor, CancellationToken ct) =>
        (await RegisterAsync(jobName, ScheduledDeliveryCode.Permanent, [Schedule("only", cursor)], ct)).SlotId!.Value;

    /// <summary>Registers the definition and its schedules again with each call; a per-occurrence definition has no slot.</summary>
    private async Task<(int DefinitionId, long? SlotId)> RegisterAsync(
        string jobName,
        ScheduledDeliveryCode delivery,
        IReadOnlyList<SlotSchedule> schedules,
        CancellationToken ct
    )
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName, delivery)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: delivery,
            Schedules: schedules
        );
        var slots = await ScheduleTestOps.RegisterAsync(Services, [definition], ct);
        return (defId, slots.TryGetValue(defId, out var slotId) ? slotId : null);
    }

    private static SlotSchedule Schedule(string name, DateTime cursor) =>
        new(name, Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor);

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

    private static JobDescriptor Def(string name, ScheduledDeliveryCode delivery = ScheduledDeliveryCode.Permanent) =>
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
            EventRecording: EventRecordingCode.Audit,
            AlertProfile: default,
            FiringFailurePolicy: FiringFailurePolicyCode.Continue,
            ScheduledDelivery: delivery,
            Invoker: null!,
            DeserializeInput: null!,
            SerializeOutput: null
        );
}
