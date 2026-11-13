using Acta.Tests.Context;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RetentionJobTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionSweep Sweep = new(42, 2, 3, 4, 10, 2, TimeSpan.MaxValue);

    [Fact]
    public async Task Sections_are_ordered_bounded_and_share_one_database_clock_read()
    {
        var clock = new Clock();
        var store = new Store(static _ => 1);
        var result = await Job(store.Handler, clock).PurgeExpiredDataAsync(Sweep, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, clock.Reads);
        Assert.Equal(14, store.Calls.Count);
        // Every section but the jobs walk, whose cycle ended, spent its two batches finding rows, so the sweep asks to
        // run again at once.
        Assert.Equal(new RetentionSweepCounts(2, 2, 2, 2, 2, 2, 2, null, BudgetSpent: true), result);
        for (var index = 0; index < 7; index++)
        {
            var first = store.Calls[index * 2];
            Assert.Equal((RetentionSection)(index + 1), first.Section);
            // The jobs walk's two calls differ only in the cursor.
            Assert.Equal(first with { Before = null }, store.Calls[index * 2 + 1] with { Before = null });
            Assert.Equal(42, first.NamespaceId);
            Assert.Equal(10, first.BatchSize);
            Assert.Equal(
                index switch
                {
                    1 => Now.AddDays(-2),
                    2 or 3 => Now.AddDays(-3),
                    4 => Now.AddDays(-2),
                    _ => Now,
                },
                first.CutoffUtc
            );
        }
    }

    [Fact]
    public async Task Empty_batch_ends_only_its_section_but_not_the_jobs_walk()
    {
        // A jobs slice with nothing expired says nothing about the slices below it, so the walk goes on.
        var store = new Store(static _ => 0);
        var result = await Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, null, TestContext.Current.CancellationToken);
        Assert.Equal(8, store.Calls.Count);
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, null), result);
    }

    [Fact]
    public async Task The_sys_events_sweep_follows_the_events_section_with_its_bounds_and_is_not_counted()
    {
        var store = new Store(static command => command.NamespaceId == 1 ? 1 : 0);
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { SweepSystemEvents = true }, null, TestContext.Current.CancellationToken);

        Assert.Equal(10, store.Calls.Count);
        // A sys backlog is a backlog too: the sweep runs again at once.
        Assert.True(result.BudgetSpent);
        Assert.Equal(RetentionSection.Events, store.Calls[2].Section);
        Assert.All(store.Calls.Skip(3).Take(2), call => Assert.Equal(store.Calls[2] with { NamespaceId = 1 }, call));
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, null, BudgetSpent: true), result);
    }

    [Theory]
    [InlineData(4, 2 * 86_400)]
    [InlineData(3 * 86_400, 3 * 86_400)]
    public async Task Terminal_workers_are_kept_for_the_longer_of_their_window_and_the_events_window(int workerSeconds, int keptSeconds)
    {
        var store = new Store(static _ => 0);
        await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { WorkerRetentionSeconds = workerSeconds }, null, TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddSeconds(-keptSeconds), store.Calls.Single(c => c.Section == RetentionSection.Workers).CutoffUtc);
    }

    [Fact]
    public async Task The_jobs_walk_continues_from_the_cursor_and_ends_with_its_cycle()
    {
        var (stored, older) = (new RetentionCursor(Now.AddDays(-5).Ticks, 500), new RetentionCursor(Now.AddDays(-70).Ticks, 70));
        var store = new Store(static _ => 1, command => command.Before == older ? null : older);
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { JobsCursor = stored }, null, TestContext.Current.CancellationToken);

        var jobs = store.Calls.Where(c => c.Section == RetentionSection.Jobs).ToList();
        Assert.Equal([stored, older], jobs.Select(c => c.Before));
        Assert.Null(result.JobsCursor);
        Assert.Equal(2, result.Jobs);
    }

    [Fact]
    public async Task Failed_later_batch_is_not_replayed_and_does_not_revisit_committed_sections()
    {
        var committed = 0;
        var store = new Store(command =>
        {
            if (command.Section == RetentionSection.Events)
            {
                throw new InvalidOperationException("failed batch");
            }
            committed++;
            return 1;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, null, TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, committed);
        Assert.Equal(3, store.Calls.Count);
        Assert.Equal(RetentionSection.Events, store.Calls[^1].Section);
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
            Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, null, cancellation.Token)
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
                .PurgeExpiredDataAsync(
                    Sweep with
                    {
                        BatchSize = batchSize,
                        MaxIterations = iterations,
                    },
                    null,
                    TestContext.Current.CancellationToken
                )
        );
        Assert.Equal(0, clock.Reads);
        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData(10, false, 2)]
    [InlineData(0, true, 1)]
    public async Task The_jobs_walk_stops_at_its_delete_budget_or_its_time_and_keeps_its_place(
        int deletedPerSlice,
        bool timeSpent,
        int expectedSlices
    )
    {
        // Ten a slice reaches the delete budget of MaxIterations x BatchSize = 20 in two slices, though the cycle goes
        // on. A walk whose time is spent stops after the slice it was reading, whatever that slice held.
        var store = new Store(_ => deletedPerSlice, Older);
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(
                Sweep with
                {
                    JobsWalkBudget = timeSpent ? TimeSpan.Zero : TimeSpan.MaxValue,
                },
                null,
                TestContext.Current.CancellationToken
            );

        Assert.Equal(expectedSlices, store.Calls.Count(c => c.Section == RetentionSection.Jobs));
        Assert.NotNull(result.JobsCursor);
    }

    [Theory]
    [InlineData(10, false, true)]
    [InlineData(0, false, false)]
    [InlineData(10, true, false)]
    public async Task An_unfinished_jobs_walk_runs_again_at_once_only_when_it_deleted(int deletedPerSlice, bool cycleEnds, bool budgetSpent)
    {
        // One slice a pass. A pass that deleted and has more to walk runs again at once; one that read only jobs still
        // kept waits for its tick, or a namespace of long-kept jobs would never rest; one whose cycle ended is done.
        var store = new Store(
            command => command.Section == RetentionSection.Jobs ? deletedPerSlice : 0,
            cycleEnds ? static _ => null : Older
        );
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { JobsWalkBudget = TimeSpan.Zero }, null, TestContext.Current.CancellationToken);

        Assert.Equal(budgetSpent, result.BudgetSpent);
    }

    [Fact]
    public async Task The_walks_place_is_kept_before_a_later_section_can_fail()
    {
        var store = new Store(
            static command => command.Section == RetentionSection.Events ? throw new InvalidOperationException("failed batch") : 0,
            Older
        );
        RetentionCursor? kept = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Job(store.Handler, new Clock())
                .PurgeExpiredDataAsync(Sweep with { JobsWalkBudget = TimeSpan.Zero }, null, TestContext.Current.CancellationToken, place =>
                {
                    kept = place;
                    return Task.CompletedTask;
                })
        );

        Assert.Equal(Older(store.Calls[0]), kept);
    }

    [Fact]
    public async Task The_handler_stores_the_walks_place_and_the_next_pass_reads_on_from_it()
    {
        var ct = TestContext.Current.CancellationToken;
        // A whole delete budget in one slice: the pass stops mid-cycle having deleted, so it runs again at once.
        var store = new Store(static command => command.Section == RetentionSection.Jobs ? 50_000 : 0, Older);
        var ctx = new RecordingJobContext();
        var job = Job(store.Handler, new Clock());

        await Assert.ThrowsAsync<RescheduleJobException>(() => job.Handle(ctx, ct));
        await Assert.ThrowsAsync<RescheduleJobException>(() => job.Handle(ctx, ct));

        var jobs = store.Calls.Where(c => c.Section == RetentionSection.Jobs).ToList();
        Assert.Null(jobs[0].Before);
        Assert.Equal(Older(jobs[0]), jobs[1].Before);
        Assert.Equal(Older(jobs[1]), await ctx.GetRequiredVariableAsync<RetentionCursor>(RetentionCursor.VariableName, ct));
    }

    [Fact]
    public async Task The_handler_stores_no_place_once_a_cycle_has_ended()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new Store(static _ => 0, static _ => null);
        var ctx = new RecordingJobContext();

        await Job(store.Handler, new Clock()).Handle(ctx, ct);

        Assert.Equal(RetentionCursor.None, await ctx.GetRequiredVariableAsync<RetentionCursor>(RetentionCursor.VariableName, ct));
    }

    // A walk that never reaches the namespace's oldest job.
    private static RetentionCursor? Older(PurgeExpiredDataCommand command) => new((command.Before?.Utc ?? Now).AddDays(-1).Ticks, 7);

    private static RetentionJob Job(MaintenanceOperations maintenance, IClockOperations clock) =>
        new(maintenance, clock, Options.Create(new JobsOptions()));

    private sealed class Clock : IClockOperations
    {
        public int Reads { get; private set; }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now.AddDays(Reads++));
    }

    /// <summary>The real purge handler over a scripted session that decodes each bound batch back into its command.</summary>
    /// <remarks>The jobs walk answers <paramref name="next"/>: by default a cursor to the call that starts a cycle and the cycle's end to the next.</remarks>
    internal sealed class Store(Func<PurgeExpiredDataCommand, int> execute, Func<PurgeExpiredDataCommand, RetentionCursor?>? next = null)
    {
        public List<PurgeExpiredDataCommand> Calls { get; } = [];

        /// <summary>The maintenance operations over this store, where the namespace has no alert projector to wait for.</summary>
        public MaintenanceOperations Handler => new ScriptedSession(call =>
                call.Stem switch
                {
                    "GetAlertFloor" => Array.Empty<GetAlertFloorRow>(),
                    _ => Purge(call),
                }
            ).Services().GetRequiredService<MaintenanceOperations>();

        private PurgeExpiredDataResponse Purge(BoundCall call)
        {
            var command = new PurgeExpiredDataCommand(
                call.Get(ActaSchema.Job.NamespaceId),
                (RetentionSection)call.Get(ActaSchema.Sql.PurgeSection),
                call.Get(ActaSchema.Sql.PurgeCutoffUtc),
                call.Get(ActaSchema.Sql.PurgeBatchSize),
                call.Get(ActaSchema.Sql.PurgeBeforeCreatedUtc) is { } before
                    ? new RetentionCursor(before.Ticks, call.Get(ActaSchema.Sql.PurgeBeforeJobId))
                    : null
            );
            Calls.Add(command);
            var cursor =
                command.Section != RetentionSection.Jobs ? null
                : next is not null ? next(command)
                : command.Before is null ? new RetentionCursor(Now.AddDays(-100).Ticks, 1000)
                : null;
            return new PurgeExpiredDataResponse(execute(command), cursor);
        }
    }
}
