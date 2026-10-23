using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RetentionJobTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionSweep Sweep = new(42, 2, 3, 4, 10, 2, MaxJobSlices: 2);

    [Fact]
    public async Task Sections_are_ordered_bounded_and_share_one_database_clock_read()
    {
        var clock = new Clock();
        var store = new Store(static _ => 1);
        var result = await Job(store.Handler, clock).PurgeExpiredDataAsync(Sweep, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, clock.Reads);
        Assert.Equal(14, store.Calls.Count);
        Assert.Equal(new RetentionSweepCounts(2, 2, 2, 2, 2, 2, 2, 999), result);
        for (var index = 0; index < 7; index++)
        {
            var first = store.Calls[index * 2];
            Assert.Equal((RetentionSection)(index + 1), first.Section);
            // The jobs walk's two calls differ only in the cursor.
            Assert.Equal(first with { BeforeJobId = 0 }, store.Calls[index * 2 + 1] with { BeforeJobId = 0 });
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
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, 999), result);
    }

    [Fact]
    public async Task The_sys_events_sweep_follows_the_events_section_with_its_bounds_and_is_not_counted()
    {
        var store = new Store(static command => command.NamespaceId == 1 ? 1 : 0);
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { SweepSystemEvents = true }, null, TestContext.Current.CancellationToken);

        Assert.Equal(10, store.Calls.Count);
        Assert.Equal(RetentionSection.Events, store.Calls[2].Section);
        Assert.All(store.Calls.Skip(3).Take(2), call => Assert.Equal(store.Calls[2] with { NamespaceId = 1 }, call));
        Assert.Equal(new RetentionSweepCounts(0, 0, 0, 0, 0, 0, 0, 999), result);
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
    public async Task The_jobs_walk_continues_from_the_cursor_and_ends_at_the_oldest_row()
    {
        var store = new Store(static _ => 1, static command => command.BeforeJobId == 70 ? 0 : 70);
        var result = await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { JobsCursor = 500 }, null, TestContext.Current.CancellationToken);

        var jobs = store.Calls.Where(c => c.Section == RetentionSection.Jobs).ToList();
        Assert.Equal([500L, 70L], jobs.Select(c => c.BeforeJobId));
        Assert.Equal(0, result.JobsCursor);
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
    [InlineData(0, 5)]
    [InlineData(10, 2)]
    public async Task The_jobs_walk_spends_its_slices_apart_from_its_deletes(int deletedPerSlice, int expectedSlices)
    {
        // Nothing expired: the walk takes every slice it is given. Ten a slice: it stops at the delete budget of
        // MaxIterations x BatchSize = 20, though slices remain.
        var store = new Store(_ => deletedPerSlice);
        await Job(store.Handler, new Clock())
            .PurgeExpiredDataAsync(Sweep with { MaxJobSlices = 5 }, null, TestContext.Current.CancellationToken);

        Assert.Equal(expectedSlices, store.Calls.Count(c => c.Section == RetentionSection.Jobs));
    }

    private static RetentionJob Job(MaintenanceOperations maintenance, IClockOperations clock) =>
        new(maintenance, clock, Options.Create(new JobsOptions()));

    private sealed class Clock : IClockOperations
    {
        public int Reads { get; private set; }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now.AddDays(Reads++));
    }

    /// <summary>The real purge handler over a scripted session that decodes each bound batch back into its command.</summary>
    /// <remarks>The jobs walk answers <paramref name="next"/>, by default one row below the cursor, never the end.</remarks>
    internal sealed class Store(Func<PurgeExpiredDataCommand, int> execute, Func<PurgeExpiredDataCommand, long>? next = null)
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
                call.Get(ActaSchema.Sql.PurgeBeforeJobId)
            );
            Calls.Add(command);
            var nextJobId =
                command.Section != RetentionSection.Jobs ? 0
                : next is not null ? next(command)
                : command.BeforeJobId == 0 ? 1000
                : command.BeforeJobId - 1;
            return new PurgeExpiredDataResponse(execute(command), nextJobId);
        }
    }
}
