using Acta.Runtime.Maintenance;
using Acta.Runtime.Services.Time;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RetentionCoordinatorTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PurgeExpiredDataCommand Sweep = new(42, 2, 3, 4, 10, 2);

    [Fact]
    public async Task Sections_are_ordered_bounded_and_share_one_database_clock_read()
    {
        var clock = new Clock();
        var store = new Store(static (_, _) => Task.FromResult(1));
        var result = await new RetentionCoordinator(store, clock).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken);

        Assert.Equal(1, clock.Reads);
        Assert.Equal(14, store.Calls.Count);
        Assert.Equal(new PurgeExpiredDataResult(2, 2, 2, 2, 2, 2), result);
        for (var index = 0; index < 7; index++)
        {
            var first = store.Calls[index * 2];
            Assert.Equal((RetentionSection)(index + 1), first.Section);
            Assert.Equal(first, store.Calls[index * 2 + 1]);
            Assert.Equal(42, first.NamespaceId);
            Assert.Equal(10, first.BatchSize);
            Assert.Equal(
                index switch
                {
                    1 => Now.AddDays(-2),
                    2 or 3 or 4 => Now.AddDays(-3),
                    5 => Now.AddSeconds(-4),
                    _ => Now,
                },
                first.CutoffUtc
            );
        }
    }

    [Fact]
    public async Task Empty_batch_ends_only_its_section()
    {
        var store = new Store(static (_, _) => Task.FromResult(0));
        var result = await new RetentionCoordinator(store, new Clock()).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken);
        Assert.Equal(7, store.Calls.Count);
        Assert.Equal(default, result);
    }

    [Fact]
    public async Task Failed_later_batch_is_not_replayed_and_does_not_revisit_committed_sections()
    {
        var committed = 0;
        var store = new Store(
            (command, _) =>
            {
                if (command.Section == RetentionSection.Events)
                {
                    throw new InvalidOperationException("failed batch");
                }
                committed++;
                return Task.FromResult(1);
            }
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RetentionCoordinator(store, new Clock()).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken)
        );
        Assert.Equal(2, committed);
        Assert.Equal(3, store.Calls.Count);
        Assert.Equal(RetentionSection.Events, store.Calls[^1].Section);
    }

    [Fact]
    public async Task Cancellation_after_a_committed_batch_stops_before_the_next_batch()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var store = new Store(
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(1);
            }
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RetentionCoordinator(store, new Clock()).PurgeExpiredDataAsync(Sweep, cancellation.Token)
        );
        Assert.Single(store.Calls);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(10, -1)]
    public async Task Invalid_bounds_fail_before_database_work(int batchSize, int iterations)
    {
        var clock = new Clock();
        var store = new Store(static (_, _) => Task.FromResult(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new RetentionCoordinator(store, clock).PurgeExpiredDataAsync(
                Sweep with
                {
                    BatchSize = batchSize,
                    MaxIterations = iterations,
                },
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(0, clock.Reads);
        Assert.Empty(store.Calls);
    }

    private sealed class Clock : IServerClock
    {
        public int Reads { get; private set; }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now.AddDays(Reads++));
    }

    private sealed class Store(Func<PurgeExpiredDataBatchCommand, CancellationToken, Task<int>> execute) : IRetentionStore
    {
        public List<PurgeExpiredDataBatchCommand> Calls { get; } = [];

        public Task<int> PurgeBatchAsync(PurgeExpiredDataBatchCommand command, CancellationToken ct)
        {
            Calls.Add(command);
            return execute(command, ct);
        }
    }
}
