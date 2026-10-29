using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Operations.Schedules;

/// <summary>
/// The atomic apply of an admission plan: a group applies whole against the versions it was planned at, an admitting
/// group also needs its slot Idle, and a stale group applies nothing so the admitter replans it.
/// </summary>
[ConformanceSpec(
    "schedule.admit-firings",
    "An admission plan applies whole per slot, or not at all when stale",
    Area = "Scheduling",
    Contract = "A planned group applies whole at the versions it read, moving the slot to Ready with its envelope and the members' cursors, and a moved group applies nothing."
)]
public sealed class AdmitFiringsSpec : ActaStorageTestBase<ConformanceFixture>
{
    private static readonly DateTime Generation = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Cron5 = "*/5 * * * *";

    private IJobs Jobs => Services.GetRequiredService<IJobs>();

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

    private async Task<AdmitFiringsCommand> PlanAsync(DateTime nowUtc, CancellationToken ct)
    {
        var due = await Services
            .GetRequiredService<GetDueSchedulesHandler>()
            .HandleAsync(new GetDueSchedulesQuery(TestNamespaceId, nowUtc, 100), ct);
        return ScheduleAdmitter.Plan(TestNamespaceId, due.Schedules, nowUtc, Services.GetRequiredService<CalendarsOperations>());
    }

    private Task<AdmitFiringsResponse> Admit(AdmitFiringsCommand plan, CancellationToken ct) =>
        Services.GetRequiredService<AdmitFiringsHandler>().HandleAsync(plan, ct);

    /// <summary>One definition with one Skip schedule whose cursor sits at <paramref name="cursor"/>; returns its slot.</summary>
    private async Task<long> RegisterAsync(string jobName, DateTime cursor, CancellationToken ct)
    {
        var defId = (await DefinitionTestOps.RegisterAsync(Services, TestNamespaceId, Generation, [Def(jobName)], ct))[jobName];
        var definition = new DefinitionSchedules(
            NamespaceId: TestNamespaceId,
            DefinitionId: defId,
            InputFormatId: 0,
            Input: ReadOnlyMemory<byte>.Empty,
            EventRecording: EventRecordingCode.Audit,
            ScheduledDelivery: ScheduledDeliveryCode.Permanent,
            Schedules: [new SlotSchedule("only", Cron5, null, MisfireStrategyCode.Skip, ScheduleExpressionKindCode.Cron, null, cursor)]
        );
        return (await ScheduleTestOps.RegisterAsync(Services, [definition], ct))[defId];
    }

    private static DateTime FloorSeconds(DateTime t) => new(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), t.Kind);

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
