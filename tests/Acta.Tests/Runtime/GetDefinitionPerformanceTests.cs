using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The performance read's map from counts to an answer: nearest-rank percentiles in whole numbers, the outcome each event
/// code counts as, every interval of the range, and duration bins that each hold a duration group whole.
/// </summary>
public sealed class GetDefinitionPerformanceTests
{
    private static readonly DateTime T0 = new(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc);

    private static readonly GetDefinitionPerformanceExecutions None = new(0, null, null, null);

    [Fact]
    public void Percentiles_take_the_nearest_rank_in_whole_numbers()
    {
        // The read answers its groups in no order.
        var four = Map(Succeeded(30), Succeeded(10), Succeeded(40), Succeeded(20));
        var one = Map(Succeeded(70));
        var tail = Map(Succeeded(9000), Succeeded(10, count: 98), Succeeded(500));

        Assert.Equal(new GetDefinitionPerformanceExecutions(4, 20, 40, 40), four.Succeeded);
        Assert.Equal(Once(70), one.Succeeded);
        // Rank 95 of 100 is still among the 98 at 10 ms, and rank 99 is the 500 ms one.
        Assert.Equal(new GetDefinitionPerformanceExecutions(100, 10, 10, 500), tail.Succeeded);
    }

    [Fact]
    public void A_failed_execution_and_a_failure_that_ended_its_job_both_count_as_failed()
    {
        var performance = Map(
            Row(EventCode.JobExecutionSucceeded, 10),
            Row(EventCode.JobExecutionFailed, 20),
            Row(EventCode.JobFailed, 30)
        );

        Assert.Equal(Once(10), performance.Succeeded);
        Assert.Equal(new GetDefinitionPerformanceExecutions(2, 20, 30, 30), performance.Failed);
        var interval = Assert.Single(performance.Intervals);
        Assert.Equal((performance.Succeeded, performance.Failed), (interval.Succeeded, interval.Failed));
        Assert.Equal([new GetDefinitionPerformanceBin(10, 13, 1)], performance.SucceededBins);
        Assert.Equal([new GetDefinitionPerformanceBin(20, 25, 1), new GetDefinitionPerformanceBin(25, 32, 1)], performance.FailedBins);
    }

    [Fact]
    public void Every_interval_of_the_range_is_answered_in_order_and_a_row_lands_in_the_one_it_names()
    {
        // 150 minutes in hours are two whole intervals and half of a third.
        var performance = GetDefinitionPerformanceHandler.Map(
            Query(T0.AddMinutes(150), 3600),
            [
                Row(EventCode.JobExecutionSucceeded, 40, intervalNumber: 2),
                Row(EventCode.JobExecutionSucceeded, 10),
                Row(EventCode.JobFailed, 20),
            ],
            GetDefinitionPerformanceHandler.ExecutionLimit
        );

        Assert.Equal([T0, T0.AddHours(1), T0.AddHours(2)], performance.Intervals.Select(static interval => interval.StartAtUtc));
        Assert.Equal(
            [(Once(10), Once(20)), (None, None), (Once(40), None)],
            performance.Intervals.Select(static interval => (interval.Succeeded, interval.Failed))
        );
        // A range of whole intervals ends without a short one.
        Assert.Equal(
            2,
            GetDefinitionPerformanceHandler
                .Map(Query(T0.AddHours(2), 3600), [], GetDefinitionPerformanceHandler.ExecutionLimit)
                .Intervals.Count
        );
    }

    [Fact]
    public void The_scale_has_a_bin_to_each_millisecond_below_10_ms_then_ten_to_each_decade()
    {
        long[] steps = [10, 13, 16, 20, 25, 32, 40, 50, 63, 80];
        List<long> edges = [.. Enumerable.Range(0, 10).Select(static ms => (long)ms)];
        for (long decade = 1; decade <= 100_000_000; decade *= 10)
        {
            edges.AddRange(steps.Select(step => step * decade));
        }

        var bins = WholeScale();

        Assert.Equal(edges.Take(bins.Count), bins.Select(static bin => bin.FromMs));
        Assert.Equal(edges.Skip(1).Take(bins.Count), bins.Select(static bin => bin.ToMs));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(12, 10, 13)]
    [InlineData(13, 13, 16)]
    [InlineData(99, 80, 100)]
    [InlineData(100, 100, 130)]
    public void A_duration_lands_in_the_bin_its_edge_or_the_edge_below_it_starts(int durationGroupMs, int fromMs, int toMs)
    {
        var bin = Assert.Single(Map(Succeeded(durationGroupMs)).SucceededBins);

        Assert.Equal(new GetDefinitionPerformanceBin(fromMs, toMs, 1), bin);
    }

    [Fact]
    public void Bins_run_from_the_first_that_holds_an_execution_to_the_last_with_the_empty_ones_between()
    {
        var bins = Map(Succeeded(99), Succeeded(12, count: 2)).SucceededBins;

        Assert.Equal([10L, 13, 16, 20, 25, 32, 40, 50, 63, 80], bins.Select(static bin => bin.FromMs));
        Assert.Equal([2L, 0, 0, 0, 0, 0, 0, 0, 0, 1], bins.Select(static bin => bin.ExecutionCount));
    }

    [Fact]
    public void Every_duration_group_the_read_answers_falls_whole_into_one_bin()
    {
        (int First, int Last)[] groups = [.. DurationGroups()];

        var split = groups.Where(static group => Map(Succeeded(group.First), Succeeded(group.Last)).SucceededBins.Count != 1);

        Assert.Equal(int.MaxValue, groups[^1].Last);
        Assert.Empty(split);
    }

    [Fact]
    public void BinLimit_is_the_bin_count_from_0_ms_to_the_longest_duration_a_worker_records()
    {
        var bins = WholeScale();

        Assert.Equal(GetDefinitionPerformanceHandler.BinLimit, bins.Count);
        Assert.Equal(0L, bins[0].FromMs);
        Assert.Equal(2_500_000_000L, bins[^1].ToMs);
    }

    [Fact]
    public void ExecutionsHasMore_is_set_when_the_rows_of_both_outcomes_reach_the_limit()
    {
        GetDefinitionPerformanceRow[] rows =
        [
            Row(EventCode.JobExecutionSucceeded, 10, count: 3),
            Row(EventCode.JobExecutionFailed, 20, count: 2),
        ];

        Assert.True(GetDefinitionPerformanceHandler.Map(Query(T0.AddMinutes(1), 60), rows, executionLimit: 5).ExecutionsHasMore);
        Assert.False(GetDefinitionPerformanceHandler.Map(Query(T0.AddMinutes(1), 60), rows, executionLimit: 6).ExecutionsHasMore);
    }

    [Fact]
    public void OldestAtUtc_is_the_least_of_the_rows()
    {
        var performance = Map(
            Row(EventCode.JobExecutionSucceeded, 10, oldestAtUtc: T0.AddSeconds(30)),
            Row(EventCode.JobExecutionFailed, 20, oldestAtUtc: T0.AddSeconds(5)),
            Row(EventCode.JobExecutionSucceeded, 40, oldestAtUtc: T0.AddSeconds(50))
        );

        Assert.Equal(T0.AddSeconds(5), performance.OldestAtUtc);
    }

    [Fact]
    public void No_rows_answer_every_interval_empty()
    {
        var performance = GetDefinitionPerformanceHandler.Map(
            Query(T0.AddMinutes(3), 60),
            [],
            GetDefinitionPerformanceHandler.ExecutionLimit
        );

        Assert.Equal((None, None), (performance.Succeeded, performance.Failed));
        Assert.Empty(performance.SucceededBins);
        Assert.Empty(performance.FailedBins);
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2)], performance.Intervals.Select(static interval => interval.StartAtUtc));
        Assert.All(performance.Intervals, static interval => Assert.Equal((None, None), (interval.Succeeded, interval.Failed)));
        Assert.False(performance.ExecutionsHasMore);
        Assert.Null(performance.OldestAtUtc);
    }

    /// <summary>
    /// The first and last duration of each group the read truncates a duration to: one per millisecond below 100 ms, then
    /// two significant figures in each decade, up to the int a worker clamps a duration to.
    /// </summary>
    private static IEnumerable<(int First, int Last)> DurationGroups()
    {
        for (var ms = 0; ms < 100; ms++)
        {
            yield return (ms, ms);
        }

        for (long size = 10; size * 10 <= int.MaxValue; size *= 10)
        {
            for (var first = size * 10; first < size * 100 && first <= int.MaxValue; first += size)
            {
                yield return ((int)first, (int)Math.Min(first + size - 1, int.MaxValue));
            }
        }
    }

    /// <summary>The bins of the shortest duration and of the longest group, int.MaxValue truncated to two significant figures.</summary>
    private static IReadOnlyList<GetDefinitionPerformanceBin> WholeScale() => Map(Succeeded(0), Succeeded(2_100_000_000)).SucceededBins;

    /// <summary>The rows over one minute from T0 in one interval, under the handler's own limit.</summary>
    private static GetDefinitionPerformanceResponse Map(params GetDefinitionPerformanceRow[] rows) =>
        GetDefinitionPerformanceHandler.Map(Query(T0.AddMinutes(1), 60), rows, GetDefinitionPerformanceHandler.ExecutionLimit);

    private static GetDefinitionPerformanceQuery Query(DateTime toUtc, int intervalSeconds) =>
        new("billing", "send-invoice", T0, toUtc, intervalSeconds);

    private static GetDefinitionPerformanceRow Succeeded(int durationGroupMs, long count = 1) =>
        Row(EventCode.JobExecutionSucceeded, durationGroupMs, count);

    private static GetDefinitionPerformanceRow Row(
        EventCode code,
        int durationGroupMs,
        long count = 1,
        int intervalNumber = 0,
        DateTime? oldestAtUtc = null
    ) => new(intervalNumber, code, durationGroupMs, count, oldestAtUtc ?? T0);

    private static GetDefinitionPerformanceExecutions Once(int durationMs) => new(1, durationMs, durationMs, durationMs);
}
