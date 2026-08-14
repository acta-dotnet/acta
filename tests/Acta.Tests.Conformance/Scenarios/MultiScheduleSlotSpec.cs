using Acta.Relational.Entities;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Minimal handler for the multi-schedule ping job. Records nothing; the spec verifies
/// cursor/slot-MIN state, not handler-observable side effects.
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
                InvocationKind: JobInvocationKind.Task,
                RequiresJobContextParameter: true,
                RequiresCancellationToken: true,
                Priority: JobPriorityCode.Normal,
                MaxAttempts: 2,
                AuditLevel: JobAuditLevelCode.Audit,
                AlertProfile: AlertProfileCode.OnFailure,
                Invoker: static async (_, _, ctx, ct) =>
                {
                    await MultiSchedulePingHandler.Run(ctx, ct);
                    return new JobHandlerInvocationResult(false, null);
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
                RecurringResultCap = 3,
            },
        ]);
}

/// <summary>
/// End-to-end conformance for multi-schedule slots: MIN cursor selection, per-schedule advance on fire,
/// and MIN recompute across all schedules after each execution.
/// </summary>
[ConformanceSpec(
    "schedule.multi-slot-min",
    "Multi-schedule slot picks MIN next_run and recomputes on fire",
    Area = "Scheduling",
    Contract = "A slot with multiple schedules arms next_run_at_utc to the MIN cursor and recomputes the MIN after each fire.",
    Arrange = "A multi-ping job carries two interval schedules, PT30S fast and PT50S slow, anchored at T0 under a fake clock.",
    Act = "The clock advances so the fast schedule fires alone at T0+30s and both schedules are due at T0+60s.",
    Assert = "The slot arms next_run_at_utc to the MIN cursor, advances only the fired schedules, and re-arms to the recomputed MIN after each fire."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class MultiScheduleSlotSpec<TFixture> : ActaRuntimeTestBase<TFixture, MultiScheduleSlotManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "multi-ping";
    private const string FastName = "fast";
    private const string SlowName = "slow";

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(50);

    // Far-past anchor so fake-derived cursors are always past-due from the real DB clock.
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register the deterministic clock BEFORE UseActa so its TryAddSingleton<IActaClock> no-ops.
        Clock = new FakeClock(T0);
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "Slot next_run_at_utc is the MIN across its two schedule cursors after registration")]
    public async Task Slot_next_run_is_min_across_schedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Anchored at T0: fast first fires at T0+30s, slow at T0+50s.
        var fastCursor = await ScheduleCursorAsync(slotId, FastName, ct);
        var slowCursor = await ScheduleCursorAsync(slotId, SlowName, ct);
        Assert.Equal(T0 + Fast, fastCursor); // T0+30s
        Assert.Equal(T0 + Slow, slowCursor); // T0+50s

        // Slot arms to the MIN: the fast schedule.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(T0 + Fast, slot.NextRunAtUtc); // T0+30s
    }

    [Fact(DisplayName = "Firing the earlier schedule advances only its cursor and recomputes slot MIN")]
    public async Task Firing_earlier_schedule_advances_only_it_and_recomputes_min()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Advance clock to T0+30s (fast cursor) and fire.
        await FireOnceAsync(slotId, ct);

        // Fast advanced one period; slow unchanged.
        var fastCursor = await ScheduleCursorAsync(slotId, FastName, ct);
        var slowCursor = await ScheduleCursorAsync(slotId, SlowName, ct);
        Assert.Equal(T0 + Fast + Fast, fastCursor); // T0+60s
        Assert.Equal(T0 + Slow, slowCursor); // T0+50s: not due, not advanced

        // Slot re-arms to the new MIN: now the slow schedule.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(T0 + Slow, slot.NextRunAtUtc); // T0+50s
        Assert.Equal(1, slot.ExecutionNumber);
    }

    [Fact(DisplayName = "Firing when both schedules are due advances both cursors and re-arms to new MIN")]
    public async Task Firing_when_both_due_advances_both_and_recomputes_min()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Advance clock to T0+60s: fast was due at T0+30s, slow at T0+50s. Both overdue.
        Clock.AdvanceTo(T0 + TimeSpan.FromSeconds(60));
        var outcome = await Runtime.RunOnceAsync(slotId, ct);
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, outcome);

        // FirstAfter math (anchor-locked, drift-free):
        //   fast: steps = floor(60/30)+1 = 3 → T0+90s
        //   slow: steps = floor(60/50)+1 = 2 → T0+100s
        var expectedFast = T0 + TimeSpan.FromSeconds(90);
        var expectedSlow = T0 + TimeSpan.FromSeconds(100);

        var fastCursor = await ScheduleCursorAsync(slotId, FastName, ct);
        var slowCursor = await ScheduleCursorAsync(slotId, SlowName, ct);
        Assert.Equal(expectedFast, fastCursor); // T0+90s
        Assert.Equal(expectedSlow, slowCursor); // T0+100s

        // Slot re-arms to the new MIN.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(expectedFast, slot.NextRunAtUtc); // T0+90s
        Assert.Equal(1, slot.ExecutionNumber);
    }

    // ---------- helpers ----------

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private async Task<DateTime> ScheduleCursorAsync(long slotId, string scheduleName, CancellationToken ct)
    {
        var rows = await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var row = rows.Single(s => s.Name == scheduleName);
        Assert.NotNull(row.NextRunAtUtc);
        return row.NextRunAtUtc!.Value;
    }

    private async Task FireOnceAsync(long slotId, CancellationToken ct)
    {
        {
            var slot = await ReadJobAsync(slotId, ct);
            Assert.NotNull(slot!.NextRunAtUtc);
            Clock.AdvanceTo(slot.NextRunAtUtc!.Value);
        }

        var outcome = await Runtime.RunOnceAsync(slotId, ct);
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, outcome);
    }
}
