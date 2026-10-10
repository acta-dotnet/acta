using Microsoft.Extensions.Options;
using Xunit;

namespace Acta.Tests.Runtime;

public sealed class RetentionJobTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RetentionSweep Sweep = new(42, 2, 3, 4, 10, 2);

    [Fact]
    public async Task Sections_are_ordered_bounded_and_share_one_database_clock_read()
    {
        var clock = new Clock();
        var store = new Store(static _ => 1);
        var result = await Job(store.Handler, clock).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken);

        Assert.Equal(1, clock.Reads);
        Assert.Equal(16, store.Calls.Count);
        Assert.Equal(new RetentionSweepCounts(2, 2, 2, 2, 2, 2, 2), result);
        for (var index = 0; index < 8; index++)
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
        var store = new Store(static _ => 0);
        var result = await Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken);
        Assert.Equal(8, store.Calls.Count);
        Assert.Equal(default, result);
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
            Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, TestContext.Current.CancellationToken)
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
            Job(store.Handler, new Clock()).PurgeExpiredDataAsync(Sweep, cancellation.Token)
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
                    TestContext.Current.CancellationToken
                )
        );
        Assert.Equal(0, clock.Reads);
        Assert.Empty(store.Calls);
    }

    private static RetentionJob Job(PurgeExpiredDataHandler purge, IClockOperations clock) =>
        new(new MaintenanceOperations(purge), clock, Options.Create(new JobsOptions()));

    private sealed class Clock : IClockOperations
    {
        public int Reads { get; private set; }

        public ValueTask<DateTime> GetUtcNowAsync(CancellationToken ct) => ValueTask.FromResult(Now.AddDays(Reads++));
    }

    /// <summary>The real purge handler over a scripted session that decodes each bound batch back into its command.</summary>
    internal sealed class Store(Func<PurgeExpiredDataCommand, int> execute)
    {
        public List<PurgeExpiredDataCommand> Calls { get; } = [];

        public PurgeExpiredDataHandler Handler =>
            new(
                new ScriptedSession(call =>
                {
                    var command = new PurgeExpiredDataCommand(
                        call.Get(ActaSchema.Job.NamespaceId),
                        (RetentionSection)call.Get(ActaSchema.Sql.PurgeSection),
                        call.Get(ActaSchema.Sql.PurgeCutoffUtc),
                        call.Get(ActaSchema.Sql.PurgeBatchSize)
                    );
                    Calls.Add(command);
                    return execute(command);
                }),
                ScriptedSession.Dialect
            );
    }
}
