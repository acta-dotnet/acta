using Acta.Tests.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RetentionJobTests
{
    internal const string Jobs = "PurgeExpiredJobs";
    internal const string Events = "PurgeExpiredEvents";
    internal const string Alerts = "PurgeExpiredAlerts";
    internal const string Workers = "PurgeExpiredWorkers";
    internal const string Locks = "PurgeExpiredLocks";
    internal const string Lanes = "PurgeUnusedLanes";

    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionSweep Sweep = new(42, "billing", 2, 10, 2, TimeSpan.MaxValue);

    [Fact]
    public async Task Deletes_run_in_order_bounded_and_share_one_database_clock_read()
    {
        var clock = new Clock();
        var store = new Store(static _ => 1);
        var result = await Job(store.Handler, clock).SweepAsync(Sweep, TestContext.Current.CancellationToken);

        Assert.Equal(1, clock.Reads);
        // Every delete but the jobs walk, which reached the end of the expired range, spent its two batches finding rows,
        // so the sweep asks to run again at once.
        Assert.Equal(new RetentionSweepCounts(2, 2, 2, 0, 2, 2, 2, null, BudgetSpent: true), result);
        Assert.Equal(
            [Jobs, Jobs, Events, Events, Alerts, Alerts, Workers, Workers, Locks, Locks, Lanes, Lanes],
            store.Calls.Select(c => c.Stem)
        );
        // Events, alerts and workers share the one window; jobs and locks are judged at now, and lanes by reference alone.
        var window = Now.AddDays(-2);
        Assert.Equal<DateTime?>(
            [Now, Now, window, window, window, window, window, window, Now, Now, null, null],
            store.Calls.Select(CutoffOf)
        );
        Assert.All(store.Calls, call => Assert.Equal(10, call.Get(ActaSchema.Sql.PurgeBatchSize)));
        Assert.All(store.Calls.Where(c => c.Stem != Locks), call => Assert.Equal(42, NamespaceOf(call)));
    }

    [Fact]
    public async Task Empty_batch_ends_only_its_delete_but_not_the_jobs_walk()
    {
        // A jobs batch that deleted nothing may be one its guards kept whole, so the walk goes on.
        var store = new Store(static _ => 0);
        var result = await Job(store.Handler, new Clock()).SweepAsync(Sweep, TestContext.Current.CancellationToken);
        Assert.Equal([Jobs, Jobs, Events, Alerts, Workers, Locks, Lanes], store.Calls.Select(c => c.Stem));
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, null), result);
    }

    [Fact]
    public async Task An_events_pass_reads_each_batch_on_from_the_last_entry_the_batch_before_it_examined()
    {
        var store = new Store(static _ => 3);
        await Job(store.Handler, new Clock()).SweepAsync(Sweep, TestContext.Current.CancellationToken);

        // A pass starts at the beginning, and this store answers each batch's last entry three past where it began.
        Assert.Equal(
            [(DateTime.MinValue, 0L), (DateTime.MinValue.AddSeconds(3), 3L)],
            store
                .Calls.Where(c => c.Stem == Events)
                .Select(c => (c.Get(ActaSchema.Sql.PurgeAfterCreatedUtc), c.Get(ActaSchema.Sql.PurgeAfterEventId)))
        );
    }

    [Fact]
    public async Task A_projector_behind_the_cutoffs_holds_back_jobs_and_events_and_nothing_else()
    {
        var read = Now.AddDays(-5);
        var store = new Store(static _ => 0, projector: new AlertsCursor(read.Ticks, 9));
        await Job(store.Handler, new Clock()).SweepAsync(Sweep, TestContext.Current.CancellationToken);

        var window = Now.AddDays(-2);
        Assert.Equal<DateTime?>([read, read, read, window, window, Now, null], store.Calls.Select(CutoffOf));
    }

    [Fact]
    public async Task A_projector_ahead_of_the_cutoffs_holds_back_nothing()
    {
        var store = new Store(static _ => 0, projector: new AlertsCursor(Now.AddDays(1).Ticks, 9));
        await Job(store.Handler, new Clock()).SweepAsync(Sweep, TestContext.Current.CancellationToken);

        var window = Now.AddDays(-2);
        Assert.Equal<DateTime?>([Now, Now, window, window, window, Now, null], store.Calls.Select(CutoffOf));
    }

    [Fact]
    public async Task A_projector_that_has_read_nothing_holds_back_every_job_and_event()
    {
        var store = new Store(static _ => 1, projector: new AlertsCursor(0L, 0L));
        var result = await Job(store.Handler, new Clock())
            .SweepAsync(Sweep with { JobsCursor = new RetentionCursor(Now.AddDays(-5).Ticks, 500) }, TestContext.Current.CancellationToken);

        // The walk keeps the place it had, and every event batch is judged before the start of time.
        Assert.DoesNotContain(store.Calls, c => c.Stem == Jobs);
        Assert.Equal(new RetentionCursor(Now.AddDays(-5).Ticks, 500), result.JobsCursor);
        Assert.All(store.Calls.Where(c => c.Stem == Events), call => Assert.Equal(DateTime.MinValue, CutoffOf(call)));
    }

    [Fact]
    public async Task The_sys_events_sweep_follows_the_namespace_events_with_its_bounds_and_is_not_counted()
    {
        var store = new Store(static call => NamespaceOf(call) == 1 ? 1 : 0);
        var result = await Job(store.Handler, new Clock())
            .SweepAsync(Sweep with { SweepSystemEvents = true }, TestContext.Current.CancellationToken);

        Assert.Equal([Jobs, Jobs, Events, Events, Events, Alerts, Workers, Locks, Lanes], store.Calls.Select(c => c.Stem));
        // A sys backlog is a backlog too: the sweep runs again at once.
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, null, BudgetSpent: true), result);
        Assert.Equal(42, NamespaceOf(store.Calls[2]));
        Assert.All(store.Calls.Skip(3).Take(2), call =>
        {
            Assert.Equal(1, NamespaceOf(call));
            Assert.Equal(CutoffOf(store.Calls[2]), CutoffOf(call));
            Assert.Equal(10, call.Get(ActaSchema.Sql.PurgeBatchSize));
        });
    }

    [Fact]
    public async Task The_retention_option_sets_the_events_alerts_and_workers_cutoff()
    {
        var store = new Store(static _ => 0, static _ => null);
        var options = new JobsOptions { Retention = TimeSpan.FromDays(7) };

        await Job(store.Handler, new Clock(), options).Handle(new RecordingJobContext(), TestContext.Current.CancellationToken);

        // The events delete runs twice, for the namespace and for sys.
        Assert.Equal([Events, Events, Alerts, Workers], store.Calls.Where(c => CutoffOf(c) == Now.AddDays(-7)).Select(c => c.Stem));
    }

    [Fact]
    public async Task The_jobs_walk_continues_from_the_cursor_and_ends_with_the_expired_range()
    {
        var (stored, later) = (new RetentionCursor(Now.AddDays(-70).Ticks, 70), new RetentionCursor(Now.AddDays(-5).Ticks, 500));
        var store = new Store(static _ => 1, call => After(call) == later ? null : later);
        var result = await Job(store.Handler, new Clock())
            .SweepAsync(Sweep with { JobsCursor = stored }, TestContext.Current.CancellationToken);

        Assert.Equal([stored, later], store.Calls.Where(c => c.Stem == Jobs).Select(After));
        Assert.Null(result.JobsCursor);
        Assert.Equal(2, result.Jobs);
    }

    [Fact]
    public async Task Failed_later_batch_is_not_replayed_and_does_not_revisit_committed_deletes()
    {
        var committed = 0;
        var store = new Store(call =>
        {
            if (call.Stem == Events)
            {
                throw new InvalidOperationException("failed batch");
            }
            committed++;
            return 1;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Job(store.Handler, new Clock()).SweepAsync(Sweep, TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, committed);
        Assert.Equal(3, store.Calls.Count);
        Assert.Equal(Events, store.Calls[^1].Stem);
    }

    [Fact]
    public async Task Cancellation_after_a_committed_batch_stops_before_the_next_batch()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new Store(_ =>
        {
            cancellation.Cancel();
            return 1;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Job(store.Handler, new Clock()).SweepAsync(Sweep, cancellation.Token)
        );
        Assert.Single(store.Calls);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10, -1)]
    public async Task Invalid_bounds_fail_before_database_work(int batchSize, int iterations)
    {
        var clock = new Clock();
        var store = new Store(static _ => 0);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Job(store.Handler, clock)
                .SweepAsync(Sweep with { BatchSize = batchSize, MaxIterations = iterations }, TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, clock.Reads);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(10, false, 2)]
    [InlineData(0, true, 1)]
    public async Task The_jobs_walk_stops_at_its_delete_budget_or_its_time_and_keeps_its_place(
        int deletedPerBatch,
        bool timeSpent,
        int expectedBatches
    )
    {
        // Ten a batch reaches the delete budget of MaxIterations x BatchSize = 20 in two batches, though the walk goes on.
        // A walk whose time is spent stops after the batch it was reading, whatever that batch held.
        var store = new Store(_ => deletedPerBatch, Later);
        var result = await Job(store.Handler, new Clock())
            .SweepAsync(
                Sweep with
                {
                    JobsWalkBudget = timeSpent ? TimeSpan.Zero : TimeSpan.MaxValue,
                },
                TestContext.Current.CancellationToken
            );

        Assert.Equal(expectedBatches, store.Calls.Count(c => c.Stem == Jobs));
        Assert.NotNull(result.JobsCursor);
    }

    [Theory]
    [InlineData(10, false, true)]
    [InlineData(0, false, false)]
    [InlineData(10, true, false)]
    public async Task An_unfinished_jobs_walk_runs_again_at_once_only_when_it_deleted(int deletedPerBatch, bool walkEnds, bool budgetSpent)
    {
        // One batch a pass. A pass that deleted and has more to walk runs again at once; one that read only jobs its
        // guards keep waits for its tick, or a namespace of such jobs would never rest; one whose walk ended is done.
        var store = new Store(call => call.Stem == Jobs ? deletedPerBatch : 0, walkEnds ? static _ => null : Later);
        var result = await Job(store.Handler, new Clock())
            .SweepAsync(Sweep with { JobsWalkBudget = TimeSpan.Zero }, TestContext.Current.CancellationToken);

        Assert.Equal(budgetSpent, result.BudgetSpent);
    }

    [Fact]
    public async Task The_walks_place_is_kept_before_a_later_delete_can_fail()
    {
        var store = new Store(static call => call.Stem == Events ? throw new InvalidOperationException("failed batch") : 0, Later);
        RetentionCursor? kept = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Job(store.Handler, new Clock())
                .SweepAsync(Sweep with { JobsWalkBudget = TimeSpan.Zero }, TestContext.Current.CancellationToken, place =>
                {
                    kept = place;
                    return Task.CompletedTask;
                })
        );

        Assert.Equal(Later(store.Calls[0]), kept);
    }

    [Fact]
    public async Task The_handler_stores_the_walks_place_and_the_next_pass_reads_on_from_it()
    {
        var ct = TestContext.Current.CancellationToken;
        // A whole delete budget in one batch: the pass stops mid-walk having deleted, so it runs again at once.
        var store = new Store(static call => call.Stem == Jobs ? 50_000 : 0, Later);
        var ctx = new RecordingJobContext();
        var job = Job(store.Handler, new Clock());

        await Assert.ThrowsAsync<RescheduleJobException>(() => job.Handle(ctx, ct));
        await Assert.ThrowsAsync<RescheduleJobException>(() => job.Handle(ctx, ct));

        var jobs = store.Calls.Where(c => c.Stem == Jobs).ToList();
        Assert.Null(After(jobs[0]));
        Assert.Equal(Later(jobs[0]), After(jobs[1]));
        Assert.Equal(Later(jobs[1]), await ctx.GetVariableAsync<RetentionCursor>(RetentionCursor.VariableName, ct));
    }

    [Fact]
    public async Task The_handler_stores_no_place_once_the_walk_has_ended()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new Store(static _ => 0, static _ => null);
        var ctx = new RecordingJobContext();

        await Job(store.Handler, new Clock()).Handle(ctx, ct);

        Assert.Equal(RetentionCursor.None, await ctx.GetVariableAsync<RetentionCursor>(RetentionCursor.VariableName, ct));
    }

    // A walk that never reaches the end of the expired range.
    private static RetentionCursor? Later(BoundCall call) => new((After(call)?.Utc ?? Now.AddDays(-100)).AddDays(1).Ticks, 7);

    // A walk starts at the start of time, which is no cursor.
    private static RetentionCursor? After(BoundCall call) =>
        call.Get(ActaSchema.Sql.PurgeAfterRetentionUtc) is var after && after > DateTime.MinValue
            ? new RetentionCursor(after.Ticks, call.Get(ActaSchema.Sql.PurgeAfterJobId))
            : null;

    // The locks delete names no namespace, and the lanes delete no cutoff.
    internal static int? NamespaceOf(BoundCall call) =>
        call.Parameters.ContainsKey(ActaSchema.Job.NamespaceId.ParameterName) ? call.Get(ActaSchema.Job.NamespaceId) : null;

    private static DateTime? CutoffOf(BoundCall call) =>
        call.Parameters.ContainsKey(ActaSchema.Sql.PurgeCutoffUtc.ParameterName) ? call.Get(ActaSchema.Sql.PurgeCutoffUtc) : null;

    private static RetentionJob Job(MaintenanceOperations maintenance, IClockOperations clock, JobsOptions? options = null) =>
        new(maintenance, clock, Options.Create(options ?? new JobsOptions()));

    private sealed class Clock : IClockOperations
    {
        public int Reads { get; private set; }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now.AddDays(Reads++));
    }

    /// <summary>The real purge handlers over a scripted session that records each bound batch and answers it.</summary>
    /// <remarks>
    /// The jobs walk answers <paramref name="next"/>: by default a cursor to the call that starts at the oldest and the end of
    /// the expired range to the next. An alerts batch answers <paramref name="undelivered"/> of what it deleted as undelivered, and the namespace's
    /// projector stands at <paramref name="projector"/>, or there is none.
    /// </remarks>
    internal sealed class Store(
        Func<BoundCall, int> execute,
        Func<BoundCall, RetentionCursor?>? next = null,
        Func<int, int>? undelivered = null,
        AlertsCursor? projector = null
    )
    {
        public List<BoundCall> Calls { get; } = [];

        public MaintenanceOperations Handler =>
            new ScriptedSession(call => call.Stem == "GetAlertFloor" ? Floor() : Purge(call))
                .Services()
                .GetRequiredService<MaintenanceOperations>();

        private GetAlertFloorRow[] Floor() =>
            projector is null
                ? []
                : [new GetAlertFloorRow(AlertsCursor.VariableName, JobPayloadFormat.Json.Id, JobPayload.Json(projector).Data.ToArray())];

        private object Purge(BoundCall call)
        {
            Calls.Add(call);
            var deleted = execute(call);
            return call.Stem switch
            {
                Jobs => new PurgeExpiredJobsResponse(
                    deleted,
                    next is not null ? next(call)
                        : After(call) is null ? new RetentionCursor(Now.AddDays(-100).Ticks, 1000)
                        : null
                ),
                Events => new PurgeExpiredEventsResponse(
                    deleted,
                    call.Get(ActaSchema.Sql.PurgeAfterCreatedUtc).AddSeconds(deleted),
                    call.Get(ActaSchema.Sql.PurgeAfterEventId) + deleted
                ),
                Alerts => new PurgeExpiredAlertsResponse(deleted, undelivered?.Invoke(deleted) ?? 0),
                Workers => new PurgeExpiredWorkersResponse(deleted),
                Locks => new PurgeExpiredLocksResponse(deleted),
                Lanes => new PurgeUnusedLanesResponse(deleted),
                _ => throw new NotSupportedException(call.Stem),
            };
        }
    }
}
