using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Minimal handler for the multi-schedule ping job. Records nothing; the spec verifies
/// cursor and envelope state, not handler-observable side effects.
/// </summary>
internal static class MultiSchedulePingHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Hand-written manifest for the multi-schedule ping job: two interval schedules on one slot.
/// Kept isolated from <c>TestJobsManifest</c> so sibling specs that count schedules/slots are unaffected.
/// </summary>
public sealed class MultiScheduleSlotManifest : IJobManifest
{
    private const string PingJobName = "multi-ping";
    private const string FastScheduleName = "fast";
    private const string SlowScheduleName = "slow";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: PingJobName,
                HandlerType: typeof(MultiSchedulePingHandler),
                MethodName: nameof(MultiSchedulePingHandler.Run),
                InputType: typeof(NoInput),
                OutputType: null,
                InputPayloadFormat: JobPayloadFormat.None,
                OutputPayloadFormat: null,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                EventRecording: EventRecordingCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await MultiSchedulePingHandler.Run(ctx, ct);
                    return new JobHandlerOutput(false, null);
                },
                DeserializeInput: static (_, _) => new NoInput(),
                SerializeOutput: null
            )
            {
                Schedules =
                [
                    new ScheduleDescriptor(
                        JobName: PingJobName,
                        ScheduleName: FastScheduleName,
                        Expression: "PT30S",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Interval,
                        Description: null,
                        Environments: []
                    ),
                    new ScheduleDescriptor(
                        JobName: PingJobName,
                        ScheduleName: SlowScheduleName,
                        Expression: "PT50S",
                        TimeZoneId: null,
                        MisfireStrategy: MisfireStrategyCode.Skip,
                        ExpressionKind: ScheduleExpressionKindCode.Interval,
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
/// End-to-end conformance for multi-schedule slots: each schedule keeps its own cursor, a firing is admitted once
/// per pass with every due schedule contributing to it, and only the contributors advance.
/// </summary>
[ConformanceSpec(
    "schedule.multi-slot-firing",
    "A multi-schedule slot takes one firing per pass from every due schedule",
    Area = "Scheduling",
    Contract = "A slot with several schedules takes one firing per pass: every due schedule contributes to it and advances its own cursor, and the rest keep theirs."
)]
public sealed class MultiScheduleSlotSpec : ActaRuntimeTestBase<ConformanceFixture, MultiScheduleSlotManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "multi-ping";
    private const string FastName = "fast";
    private const string SlowName = "slow";

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(50);

    // Far-past anchor so an admitted firing is always due on the real DB clock.
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register the deterministic clock BEFORE UseActa so its TryAddSingleton<IActaClock> no-ops.
        Clock = new FakeClock(T0);
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "After registration each schedule holds its own first occurrence and the Idle slot carries none")]
    public async Task Each_schedule_holds_its_own_cursor_and_the_slot_none()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Anchored at T0: fast first fires at T0+30s, slow at T0+50s.
        Assert.Equal(T0 + Fast, await ScheduleCursorAsync(slotId, FastName, ct));
        Assert.Equal(T0 + Slow, await ScheduleCursorAsync(slotId, SlowName, ct));

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "A firing of the earlier schedule names only it and advances only its cursor")]
    public async Task Firing_the_earlier_schedule_advances_only_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // At T0+30s only the fast schedule is due.
        await AdmitAtAsync(slotId, T0 + Fast, ct);
        Assert.Equal([FastName], await ContributorsAsync(slotId, ct));
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // Fast advanced one period; slow unchanged.
        Assert.Equal(T0 + Fast + Fast, await ScheduleCursorAsync(slotId, FastName, ct)); // T0+60s
        Assert.Equal(T0 + Slow, await ScheduleCursorAsync(slotId, SlowName, ct)); // T0+50s: not due, not advanced

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Equal(1, slot.ExecutionNumber);
    }

    [Fact(DisplayName = "When both schedules are due, one firing carries both and advances both cursors")]
    public async Task Firing_when_both_due_carries_both_and_advances_both()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // At T0+60s fast was due at T0+30s and slow at T0+50s: both contribute to the one firing.
        await AdmitAtAsync(slotId, T0 + TimeSpan.FromSeconds(60), ct);
        Assert.Equal([FastName, SlowName], await ContributorsAsync(slotId, ct));
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // FirstAfter math (anchor-locked, drift-free):
        //   fast: steps = floor(60/30)+1 = 3 -> T0+90s
        //   slow: steps = floor(60/50)+1 = 2 -> T0+100s
        Assert.Equal(T0 + TimeSpan.FromSeconds(90), await ScheduleCursorAsync(slotId, FastName, ct));
        Assert.Equal(T0 + TimeSpan.FromSeconds(100), await ScheduleCursorAsync(slotId, SlowName, ct));

        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Equal(1, slot.ExecutionNumber);
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private async Task<DateTime> ScheduleCursorAsync(long slotId, string scheduleName, CancellationToken ct)
    {
        var row = (await ScheduleTestOps.ReadAsync(Db, slotId, ct)).Single(s => s.ScheduleName == scheduleName);
        Assert.NotNull(row.NextOccurrenceAtUtc);
        return row.NextOccurrenceAtUtc!.Value;
    }

    /// <summary>The schedule names the pending firing's envelope carries, in name order.</summary>
    private async Task<IReadOnlyList<string>> ContributorsAsync(long slotId, CancellationToken ct)
    {
        var firing = Assert.Single(
            await Db.From<CheckpointEntity>().Where(c => c.JobId == slotId && c.Kind == CheckpointKindCode.Firing).ToListAsync(ct)
        );
        Assert.Equal(CheckpointStatusCode.Pending, firing.Status);
        var envelope = FiringEnvelope.FromJson(firing.Value);
        Assert.NotNull(envelope);
        return [.. envelope.Contributors.Select(c => c.ScheduleName)];
    }

    // Runs one admission pass at the fake instant and checks it admitted the slot's firing.
    private async Task AdmitAtAsync(long slotId, DateTime nowUtc, CancellationToken ct)
    {
        Clock.AdvanceTo(nowUtc);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }
}
