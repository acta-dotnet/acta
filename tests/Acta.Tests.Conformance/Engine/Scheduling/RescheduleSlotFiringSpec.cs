using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Scenarios;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>Succeeds at once; a slot with nothing to do.</summary>
internal static class SlotHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>One Permanent definition with a five-minute schedule, parked far from any occurrence by the clock.</summary>
public sealed class RescheduleSlotManifest : IJobManifest
{
    public const string JobName = "slot-run-now";
    public const string ScheduleName = "every-5-minutes";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: JobName,
                HandlerType: typeof(SlotHandler),
                MethodName: nameof(SlotHandler.Run),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 3,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.None,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await SlotHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            )
            {
                Schedules =
                [
                    new ScheduleDescriptor(
                        JobName: JobName,
                        ScheduleName: ScheduleName,
                        Expression: "*/5 * * * *",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Cron,
                        Description: null,
                        Environments: []
                    ),
                ],
                CreateDefaultInput = static () => new NoInput(),
                SerializeInput = null,
            },
        ]);
}

/// <summary>
/// The job page's "run now" on a permanent slot: a reschedule of an Idle slot, or of a Paused one with no firing
/// outstanding, is a manual firing at the instant (now when none is given), the same envelope a schedule trigger
/// writes, and the slot returns to Idle when it settles; a slot mid-firing keeps its firing and moves its instant.
/// </summary>
[ConformanceSpec(
    "job.reschedule-slot",
    "A reschedule of an idle permanent slot is a manual firing at the instant",
    Area = "Scheduling",
    Contract = "RescheduleAsync on an Idle slot, or a Paused one with no firing, admits a manual firing at the instant, now when null, while a slot mid-firing keeps its firing."
)]
public sealed class RescheduleSlotFiringSpec : ActaRuntimeTestBase<ConformanceFixture, RescheduleSlotManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // The clock starts at the real minute: settlement stamps the DB clock, which this clock must not trail.
        var now = DateTime.UtcNow;
        Clock = new FakeClock(new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc));
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A reschedule with no instant fires an Idle slot now with a manual envelope, and the firing settles the slot Idle")]
    public async Task An_idle_slot_rescheduled_to_now_takes_a_manual_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), reasonMessage: "run now", actor: "ops", ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, result.Status);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.NotNull(slot.NextRunAtUtc);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(FiringEnvelope.ManualKind, firing.Kind);
        Assert.Empty(firing.Contributors);
        Assert.Equal("ops", EventDetail(await ReadSingleEventAsync(slotId, EventCode.JobRescheduled, ct), "actor"));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Succeeded, (await EnvelopeAsync(slotId, ct)).Status);
    }

    [Fact(DisplayName = "A Paused slot with no firing outstanding rescheduled to an instant fires at it with a manual envelope")]
    public async Task A_paused_slot_without_a_firing_takes_a_manual_firing_at_the_instant()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);
        Assert.Equal(JobStatusCode.Paused, (await ReadJobAsync(slotId, ct)).Status);
        var at = (await Clock.GetUtcNowAsync(ct)).AddMinutes(1);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), at, ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Ready, slot.Status);
        Assert.Equal(at, slot.NextRunAtUtc);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(FiringEnvelope.ManualKind, firing.Kind);
    }

    [Fact(DisplayName = "A slot paused mid-firing keeps its firing when rescheduled: only the instant moves")]
    public async Task A_slot_mid_firing_keeps_its_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, RescheduleSlotManifest.JobName, ct);
        var occurrence = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct)).NextOccurrenceAtUtc!.Value;
        Clock.AdvanceTo(occurrence);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        var admitted = (await EnvelopeAsync(slotId, ct)).Firing;
        Assert.Equal(ControlAction.Applied, (await Jobs.PauseAsync(JobLookup.ById(slotId), ct: ct)).Action);

        var result = await Jobs.RescheduleAsync(JobLookup.ById(slotId), ct: ct);

        Assert.Equal(ControlAction.Applied, result.Action);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        var (status, firing) = await EnvelopeAsync(slotId, ct);
        Assert.Equal(CheckpointStatusCode.Pending, status);
        Assert.Equal(admitted.FiringId, firing.FiringId);
        Assert.Equal(FiringEnvelope.ScheduledKind, firing.Kind);
    }

    private async Task<(CheckpointStatusCode? Status, FiringEnvelope Firing)> EnvelopeAsync(long jobId, CancellationToken ct)
    {
        var checkpoint = await Db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Firing)
            .SingleOrDefaultAsync(ct);
        Assert.NotNull(checkpoint);
        var firing = FiringEnvelope.FromJson(checkpoint.Value);
        Assert.NotNull(firing);
        return (checkpoint.Status, firing);
    }
}
