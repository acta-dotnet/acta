using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The admission loop between passes, over a scripted database: a pass that admitted and left more due is followed at
/// once on the horizon that pass read, and a pass that admitted nothing because another writer moved every group first
/// sleeps before the next, so a run of lost races never spins against the database.
/// </summary>
public sealed class ScheduleAdmitterLoopTests
{
    private const string Namespace = "orders";

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_pass_that_admitted_and_left_more_due_is_followed_at_once_without_reading_the_horizon_again()
    {
        using var script = new Script(
            horizons: [Now, Now, null],
            dueLists:
            [
                [Due()],
                [],
            ],
            applied: true
        );
        var options = new JobsOptions();

        await script.RunAsync(options);

        // The second pass starts on the horizon the first pass read, with no read of the loop's own between them.
        Assert.Equal(
            [
                "GetScheduleHorizon",
                "ListDueSchedules",
                "AdmitScheduleFirings",
                "GetScheduleHorizon",
                "ListDueSchedules",
                "GetScheduleHorizon",
                "wait",
            ],
            script.Steps
        );
        Assert.Equal([options.SafetyPollInterval], script.Waits);
    }

    [Fact]
    public async Task A_pass_that_lost_every_group_to_another_writer_sleeps_before_the_next()
    {
        using var script = new Script(
            horizons: [Now],
            dueLists:
            [
                [Due()],
            ],
            applied: false
        );
        var options = new JobsOptions();

        await script.RunAsync(options);

        // One pass replans until it gives up, reads the horizon, and waits the floor: the next pass waits for the sleep.
        string[] replan = ["ListDueSchedules", "AdmitScheduleFirings"];
        Assert.Equal(["GetScheduleHorizon", .. replan, .. replan, .. replan, .. replan, "GetScheduleHorizon", "wait"], script.Steps);
        Assert.Equal([options.MinPollFloor], script.Waits);
    }

    [Fact]
    public async Task A_pass_asked_for_a_namespace_that_is_not_the_runtimes_is_refused_before_any_read()
    {
        using var script = new Script(
            horizons: [Now],
            dueLists:
            [
                [Due()],
            ],
            applied: true
        );

        // A host of one runtime hands it every call, so the pass itself refuses a name that is not its own.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            script.Admitter(new JobsOptions()).AdmitOnceAsync("billing", TestContext.Current.CancellationToken)
        );

        Assert.Contains("'orders', not 'billing'", refused.Message, StringComparison.Ordinal);
        Assert.Empty(script.Steps);
    }

    // A per-occurrence schedule one second overdue, which a pass admits under CatchUpAll.
    private static DueSchedule Due() =>
        new(
            ScheduleId: 7,
            NamespaceId: 1,
            NamespaceName: Namespace,
            JobId: null,
            ScheduleName: "every-minute",
            DefinitionName: "report",
            Expression: "PT1M",
            TimeZoneId: null,
            ExpressionKind: ScheduleExpressionKindCode.Interval,
            MisfireStrategy: MisfireStrategyCode.CatchUpAll,
            IntervalAnchorAtUtc: null,
            AvailabilityCalendarName: null,
            StartAtUtc: null,
            EndBeforeUtc: null,
            MaxOutstanding: 1,
            FullBehavior: ScheduleFullBehaviorCode.Wait,
            ExpireAfterSeconds: null,
            ExpireAction: null,
            Status: ScheduleStatusCode.Active,
            PausedUntilUtc: null,
            ScheduleVersion: 1,
            NextOccurrenceAtUtc: Now.AddSeconds(-1),
            NextEvaluationAtUtc: Now.AddSeconds(-1),
            EvaluationStatus: ScheduleEvaluationStatusCode.Scheduled,
            OutstandingCount: 0,
            StateVersion: 1,
            SlotStatus: null,
            SlotVersion: null,
            FiringStatus: null
        );

    /// <summary>
    /// The database a loop runs against: each horizon read answers the next scripted horizon, the last one repeating, and
    /// likewise each due read; every apply answers the one group applied or stale. The first wait stops the loop, and so
    /// does an apply past the fourth, which only a loop that never waits reaches.
    /// </summary>
    private sealed class Script(DateTime?[] horizons, DueSchedule[][] dueLists, bool applied) : IWorkerWakeup, IActaClock, IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _horizonReads;
        private int _dueReads;
        private int _applies;

        public List<string> Steps { get; } = [];

        public List<TimeSpan> Waits { get; } = [];

        public Task RunAsync(JobsOptions options) =>
            Admitter(options).RunAsync(Namespace, _stop.Token).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        public ScheduleAdmitter Admitter(JobsOptions options)
        {
            var session = new ScriptedSession(Answer);
            var services = session.Services();
            var context = new WorkerContext(new WorkerRegistration(Namespace, null, null, [], []));
            context.NamespaceId = 1;
            context.WorkerId = 1;
            return new ScheduleAdmitter(
                services.GetRequiredService<SchedulesOperations>(),
                this,
                context,
                this,
                new WorkerWakeupPublisher(this),
                options,
                services.GetRequiredService<CalendarsOperations>(),
                NullLogger.Instance,
                metrics: null
            );
        }

        private object? Answer(BoundCall call)
        {
            Steps.Add(call.Stem);
            switch (call.Stem)
            {
                case "GetScheduleHorizon":
                    return new GetScheduleHorizonRow(horizons[Math.Min(_horizonReads++, horizons.Length - 1)], Now);
                case "ListDueSchedules":
                    return dueLists[Math.Min(_dueReads++, dueLists.Length - 1)];
                case "AdmitScheduleFirings":
                    if (++_applies > 4)
                    {
                        _stop.Cancel();
                        throw new OperationCanceledException(_stop.Token);
                    }
                    return new List<AdmitScheduleFiringsRow> { new(0, applied) };
                default:
                    throw new NotSupportedException(call.Stem);
            }
        }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now);

        public ValueTask WakeAsync(WorkerWakeupChannel channel, WorkerWakeupReason reason, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkerWakeupWaitStatus> WaitAsync(WorkerWakeupChannel channel, TimeSpan timeout, CancellationToken ct)
        {
            Steps.Add("wait");
            Waits.Add(timeout);
            _stop.Cancel();
            return ValueTask.FromResult(WorkerWakeupWaitStatus.TimedOut);
        }

        public void Dispose() => _stop.Dispose();
    }
}
