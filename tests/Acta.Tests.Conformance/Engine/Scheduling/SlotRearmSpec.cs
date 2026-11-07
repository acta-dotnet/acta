using System.Collections.Concurrent;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Engine.Scheduling;

/// <summary>A permanent slot whose handler re-arms itself once per firing, as <c>sys.retention</c> does with a backlog.</summary>
internal static class RearmingSlotHandler
{
    private static readonly ConcurrentDictionary<string, int> _runs = new(StringComparer.Ordinal);

    public static int Runs(string jobNamespace) => _runs.TryGetValue(jobNamespace, out var n) ? n : 0;

    public static async Task Run(JobContext ctx, CancellationToken ct)
    {
        if (_runs.AddOrUpdate(ctx.JobNamespace, 1, static (_, n) => n + 1) == 1)
        {
            await ctx.RescheduleAsync(TimeSpan.Zero, "more to do", ct);
        }
    }
}

/// <summary>One Permanent definition on an every-minute schedule whose handler re-arms its first run.</summary>
public sealed class SlotRearmManifest : IJobManifest
{
    public const string JobName = "slot-rearm";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: JobName,
                HandlerType: typeof(RearmingSlotHandler),
                MethodName: nameof(RearmingSlotHandler.Run),
                InputType: typeof(NoInput),
                ResultType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                ResultPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 1,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.None,
                FiringFailurePolicy: FiringFailurePolicyCode.Continue,
                ScheduledDelivery: ScheduledDeliveryCode.Permanent,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await RearmingSlotHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeResult: null
            )
            {
                Schedules =
                [
                    new ScheduleDescriptor(
                        JobName: JobName,
                        ScheduleName: "every-minute",
                        Expression: "* * * * *",
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
/// A permanent slot's handler that re-arms with no delay keeps its firing outstanding and runs again at once; the
/// firing settles, and the slot goes Idle, only when an attempt finishes. Retention relies on it to sweep a backlog
/// back to back.
/// </summary>
[ConformanceSpec(
    "schedule.slot-rearm",
    "A permanent slot that re-arms itself runs again within the same firing",
    Area = "Scheduling",
    Contract = "A slot handler's zero-delay reschedule keeps its firing outstanding and readies the slot until an attempt finishes, which settles the firing."
)]
public sealed class SlotRearmSpec : ActaRuntimeTestBase<ConformanceFixture, SlotRearmManifest>
{
    protected override bool ParkScheduleSlots => false;

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        Clock = FakeClock.AtRealMinute();
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "A slot that re-arms runs twice for one firing and then goes Idle with the firing settled")]
    public async Task A_rearming_slot_runs_again_within_its_firing()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await ScheduleTestOps.SlotIdAsync(Db, TestNamespace, SlotRearmManifest.JobName, ct);
        Clock.AdvanceTo(Clock.UtcNow.AddMinutes(1).AddSeconds(30));
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);

        await Runtime.RunOnceAsync(slotId, ct);
        Assert.Equal(1, RearmingSlotHandler.Runs(TestNamespace));
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
        Assert.Equal(CheckpointStatusCode.Pending, await FiringStatusAsync(slotId, ct));

        Assert.Equal(RunOnceStatus.Completed, await Runtime.RunOnceAsync(slotId, ct));
        Assert.Equal(2, RearmingSlotHandler.Runs(TestNamespace));
        Assert.Equal(JobStatusCode.Idle, (await ReadJobAsync(slotId, ct)).Status);
        Assert.NotEqual(CheckpointStatusCode.Pending, await FiringStatusAsync(slotId, ct));
        Assert.Equal(1, await CountEventsAsync(slotId, EventCode.ScheduleAdmitted, ct));
    }

    private async Task<CheckpointStatusCode?> FiringStatusAsync(long slotId, CancellationToken ct) =>
        (
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).SingleOrDefaultAsync(ct)
        )?.Status;
}
