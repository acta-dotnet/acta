using System.Collections.Immutable;
using Acta.Features.Execution;
using Acta.Features.Schedules;
using Acta.Payloads;
using Acta.Relational.Entities;
using Acta.Services.Time;
using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Scenarios;

/// <summary>
/// Minimal handler for the interval-ping recurring job. Records nothing; the spec verifies
/// cursor/execution-number state, not handler-observable side effects.
/// </summary>
internal static class IntervalPingHandler
{
    public static Task Run(JobContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Hand-written manifest for the interval-ping job (no source generator needed for a
/// spec-local manifest). Keeps the interval-ping definition isolated from
/// <c>TestJobsManifest</c> so sibling specs that count schedules/slots are unaffected.
/// </summary>
public sealed class IntervalPingManifest : IActaManifest
{
    private const string PingJobName = "interval-ping";
    private const string PingScheduleName = "every-30s";

    public static JobDescriptorManifest Descriptors { get; } =
        new(
            ImmutableArray.Create(
                new JobDescriptor(
                    JobName: PingJobName,
                    HandlerType: typeof(IntervalPingHandler),
                    MethodName: nameof(IntervalPingHandler.Run),
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
                    AlertProfile: JobAlertProfileCode.OnFailure,
                    Invoker: static async (_, _, ctx, ct) =>
                    {
                        await IntervalPingHandler.Run(ctx, ct);
                        return new JobHandlerInvocationResult(false, null);
                    },
                    DeserializeInput: static (_, _) => new NoInput(),
                    SerializeOutput: null
                )
                {
                    Schedules = ImmutableArray.Create(
                        new JobScheduleDescriptor(
                            JobName: PingJobName,
                            ScheduleName: PingScheduleName,
                            Expression: "PT30S",
                            TimeZone: null,
                            Misfire: MisfireStrategyCode.Skip,
                            ExpressionKind: ScheduleExpressionKindCode.Interval,
                            Description: null,
                            Environments: ImmutableArray<string>.Empty
                        )
                    ),
                    CreateDefaultInput = static () => new NoInput(),
                    SerializeInput = null,
                    RecurringResultCap = 3,
                }
            )
        );
}

/// <summary>
/// End-to-end conformance for ISO 8601 interval schedules: cursor advance, miss coalescing
/// (Skip misfire), and exclusive single-claim under worker contention.
/// </summary>
[ConformanceSpec(
    "schedule.interval-fire",
    "Interval slot fires end-to-end advancing cursors and coalescing misses",
    Area = "Scheduling",
    Contract = "An interval slot fires, advances its cursor by exactly one period, coalesces misses with Skip, and claims exclusively under contention.",
    Arrange = "An interval-ping job carries a PT30S ISO 8601 schedule with Skip misfire under a fake clock.",
    Act = "The clock advances so the slot fires on time, catches up over a 3.5-interval overdue window, and is claimed under worker contention.",
    Assert = "The cursor advances by exactly one period per fire, misses coalesce into one run, and only one contender claims the due slot."
)]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.RegisterScheduledJobsAsync))]
[CoversStoreMethod(typeof(IScheduleStore), nameof(IScheduleStore.GetLiveSchedulesAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.ClaimBatchAsync))]
[CoversStoreMethod(typeof(IExecutionStore), nameof(IExecutionStore.CompleteExecutionAsync))]
public abstract class IntervalScheduleFireSpec<TFixture> : ActaRuntimeTestBase<TFixture, IntervalPingManifest>
    where TFixture : IConformanceFixture, new()
{
    // Live slot cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "interval-ping";

    // PT30S: every 30 seconds, drift-free, anchor-locked.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // Far-past anchor so fake-derived cursors are always past-due from the real DB clock.
    private static readonly DateTime T0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private FakeClock Clock { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        // Register the deterministic clock BEFORE UseActa so its TryAddSingleton<IActaClock> no-ops.
        Clock = new FakeClock(T0);
        services.AddSingleton<IActaClock>(Clock);
        base.ConfigureServices(services, testNamespace);
    }

    [Fact(DisplayName = "Interval cursor advances exactly one period on a clean single fire")]
    public async Task Interval_cursor_advances_exactly_one_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Initial cursor: first occurrence strictly after T0 = T0 + PT30S.
        var before = await ScheduleCursorAsync(slotId, ct);
        Assert.Equal(T0 + Interval, before);

        await FireOnceAsync(slotId, ct);

        // Cursor rolled forward exactly one interval (drift-free, anchor-locked).
        var after = await ScheduleCursorAsync(slotId, ct);
        Assert.Equal(before + Interval, after);

        // Slot's hot-path cursor tracks the single-schedule MIN.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(after, slot.NextRunAtUtc);
        Assert.Equal(1, slot.ExecutionNumber);

        // Audit events confirm a complete recurring lifecycle.
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var codes = events.Select(e => e.JobEventCode).ToHashSet();
        Assert.Contains(JobEventCode.JobExecutionStarted, codes);
        Assert.Contains(JobEventCode.JobExecutionFinished, codes);
        Assert.Contains(JobEventCode.JobRecurringRolledOver, codes);
    }

    [Fact(DisplayName = "Missed periods are coalesced into a single fire with Skip misfire")]
    public async Task Missed_periods_coalesce_into_one_fire_with_skip()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Anchor is the initial cursor: T0 + 30s.
        // Advance past 3.5 intervals from the anchor: 3.5 * 30s = 105s → now = T0 + 135s.
        var anchor = T0 + Interval;
        var now = anchor + TimeSpan.FromSeconds(105);
        Clock.AdvanceTo(now);

        var outcome = await Runtime.RunOnceAsync(slotId, ct);
        Assert.NotEqual(RunOnceOutcome.NothingClaimed, outcome);

        // Exactly one execution despite 3.5 missed periods: Skip coalesces them.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(1, slot.ExecutionNumber);

        // Cursor jumps to the first occurrence strictly after now (anchor-locked, drift-free):
        // FirstAfter(anchor, now, 30s): steps = floor(105/30) + 1 = 4 → next = anchor + 120s = T0 + 150s.
        var expectedNext = anchor + TimeSpan.FromSeconds(120);
        Assert.Equal(expectedNext, await ScheduleCursorAsync(slotId, ct));
        Assert.Equal(expectedNext, slot.NextRunAtUtc);
    }

    [Fact(DisplayName = "A due slot is claimed exactly once under sequential worker contention")]
    public async Task Due_slot_claims_exactly_once_under_contention()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, nsId, ct);

        // Real DB clock >> 2024-epoch cursor: the slot is always past-due without advancing the fake clock.

        // Worker 1 claims the slot (Ready → Dispatched, execution_number bumped to 1).
        var claim1 = (
            await Services
                .GetRequiredService<IExecutionStore>()
                .ClaimOneAsync(new ClaimRequest(nsId, workerId, MaxBatch: 1), leaseTtlSeconds: 60, slotId, ct)
        ).Jobs;
        Assert.Single(claim1);
        Assert.Equal(1, claim1[0].ExecutionNumber);

        // Worker 2 attempts the same slot: finds it Dispatched, not Ready.
        var claim2 = (
            await Services
                .GetRequiredService<IExecutionStore>()
                .ClaimOneAsync(new ClaimRequest(nsId, workerId, MaxBatch: 1), leaseTtlSeconds: 60, slotId, ct)
        ).Jobs;
        Assert.Empty(claim2);

        // The job row confirms exactly one execution_number increment.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(1, slot.ExecutionNumber);
    }

    // ---------- helpers ----------

    private async Task<long> SlotIdAsync(CancellationToken ct)
    {
        var id = await Jobs.ResolveJobIdAsync(JobLookup.ByDeduplicationKey(TestNamespace, JobName), ct);
        Assert.NotNull(id);
        return id!.Value;
    }

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var rows = await Db.From<JobSchedule>().Where(s => s.JobId == slotId).ToListAsync(ct);
        var row = Assert.Single(rows);
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
