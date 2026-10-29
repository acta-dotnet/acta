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
public sealed class IntervalPingManifest : IJobManifest
{
    private const string PingJobName = "interval-ping";
    private const string PingScheduleName = "every-30s";

    public static JobDescriptorManifest Descriptors { get; } =
        new([
            new JobDescriptor(
                JobName: PingJobName,
                HandlerType: typeof(IntervalPingHandler),
                MethodName: nameof(IntervalPingHandler.Run),
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
                    await IntervalPingHandler.Run(ctx, ct);
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
                        ScheduleName: PingScheduleName,
                        Expression: "PT30S",
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
/// End-to-end conformance for ISO 8601 interval schedules: cursor advance per admitted firing, miss
/// coalescing (Skip misfire) at admission, and exclusive single-claim of an admitted slot under worker contention.
/// </summary>
[ConformanceSpec(
    "schedule.interval-fire",
    "Interval slot fires end-to-end advancing cursors and coalescing misses",
    Area = "Scheduling",
    Contract = "An interval slot fires, advances its cursor by exactly one period, skips misses with Skip to fire the latest once, and claims exclusively under contention."
)]
public sealed class IntervalScheduleFireSpec : ActaRuntimeTestBase<ConformanceFixture, IntervalPingManifest>
{
    // Live schedule cursors are this spec's subject; the harness default would park them.
    protected override bool ParkScheduleSlots => false;

    private const string JobName = "interval-ping";

    // PT30S: every 30 seconds, drift-free, anchor-locked.
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

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

    [Fact(DisplayName = "Interval cursor advances exactly one period on an admitted firing")]
    public async Task Interval_cursor_advances_exactly_one_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // Initial cursor: first occurrence strictly after T0 = T0 + PT30S.
        var before = await ScheduleCursorAsync(slotId, ct);
        Assert.Equal(T0 + Interval, before);

        await AdmitAtAsync(slotId, before, ct);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // Cursor rolled forward exactly one interval (drift-free, anchor-locked).
        var after = await ScheduleCursorAsync(slotId, ct);
        Assert.Equal(before + Interval, after);

        // The slot is back to Idle with no cursor of its own; the schedule carries the next occurrence.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(JobStatusCode.Idle, slot.Status);
        Assert.Null(slot.NextRunAtUtc);
        Assert.Equal(1, slot.ExecutionNumber);

        // Audit events confirm a complete recurring lifecycle.
        var events = await GetEventsByJobId.Run(Services, slotId, ct);
        var codes = events.Select(e => e.EventCode).ToHashSet();
        Assert.Contains(EventCode.ScheduleAdmitted, codes);
        Assert.Contains(EventCode.JobExecutionSucceeded, codes);
    }

    [Fact(DisplayName = "Missed periods are skipped under Skip and the latest one fires once")]
    public async Task Missed_periods_are_skipped_and_the_latest_fires_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        // The first occurrence is T0 + 30s. Advance 3.5 intervals past it: now = T0 + 135s, with occurrences at
        // +30, +60, +90, and +120 undecided. The latest is within the misfire grace, so it fires; the rest are skipped.
        var first = T0 + Interval;
        await AdmitAtAsync(slotId, first + TimeSpan.FromSeconds(105), ct);
        Assert.NotEqual(RunOnceStatus.NothingClaimed, await Runtime.RunOnceAsync(slotId, ct));

        // Exactly one execution despite 3.5 missed periods.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(1, slot.ExecutionNumber);

        // Cursor is the first occurrence strictly after the fired one (anchor-locked, drift-free): T0 + 150s.
        Assert.Equal(first + TimeSpan.FromSeconds(120), await ScheduleCursorAsync(slotId, ct));

        var codes = (await GetEventsByJobId.Run(Services, slotId, ct)).Select(e => e.EventCode).ToHashSet();
        Assert.Contains(EventCode.ScheduleSkipped, codes);
        Assert.Contains(EventCode.ScheduleAdmitted, codes);
    }

    [Fact(DisplayName = "An admitted slot is claimed exactly once under sequential worker contention")]
    public async Task Admitted_slot_claims_exactly_once_under_contention()
    {
        var ct = TestContext.Current.CancellationToken;
        var slotId = await SlotIdAsync(ct);

        var nsId = Runtime.RegisteredNamespaceIds[TestNamespace];
        var workerId = await ChaosSpecHelpers.WorkerIdAsync(Db, nsId, ct);

        // The admitted firing is due on the real DB clock at once.
        await AdmitAtAsync(slotId, T0 + Interval, ct);

        // Worker 1 claims the slot (Ready -> Executing, execution_number bumped to 1).
        var claim1 = (
            await Services
                .GetRequiredService<ExecutionOperations>()
                .ClaimJobAsync(new ClaimOptions(nsId, workerId, MaxBatch: 1), leaseTtlSeconds: 60, slotId, ct)
        ).Jobs;
        Assert.Single(claim1);
        Assert.Equal(1, claim1[0].ExecutionNumber);

        // Worker 2 attempts the same slot: finds it Executing, not Ready.
        var claim2 = (
            await Services
                .GetRequiredService<ExecutionOperations>()
                .ClaimJobAsync(new ClaimOptions(nsId, workerId, MaxBatch: 1), leaseTtlSeconds: 60, slotId, ct)
        ).Jobs;
        Assert.Empty(claim2);

        // The job row confirms exactly one execution_number increment.
        var slot = await ReadJobAsync(slotId, ct);
        Assert.Equal(1, slot.ExecutionNumber);
    }

    private Task<long> SlotIdAsync(CancellationToken ct) => ScheduleTestOps.SlotIdAsync(Db, TestNamespace, JobName, ct);

    private async Task<DateTime> ScheduleCursorAsync(long slotId, CancellationToken ct)
    {
        var row = Assert.Single(await ScheduleTestOps.ReadAsync(Db, slotId, ct));
        Assert.NotNull(row.NextOccurrenceAtUtc);
        return row.NextOccurrenceAtUtc!.Value;
    }

    // Runs one admission pass at the fake instant and checks it admitted the slot's firing.
    private async Task AdmitAtAsync(long slotId, DateTime nowUtc, CancellationToken ct)
    {
        Clock.AdvanceTo(nowUtc);
        await Runtime.AdmitOnceAsync(TestNamespace, ct);
        Assert.Equal(JobStatusCode.Ready, (await ReadJobAsync(slotId, ct)).Status);
    }
}
