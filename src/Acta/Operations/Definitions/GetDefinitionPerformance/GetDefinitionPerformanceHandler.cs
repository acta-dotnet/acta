namespace Acta;

/// <summary>
/// <see cref="IDefinitions.GetPerformanceAsync"/>: resolves the natural key, has the database count the range's outcome
/// events per interval, outcome and duration, and takes the percentiles and the histogram bins from those counts.
/// </summary>
internal sealed class GetDefinitionPerformanceHandler(GetDefinitionIdHandler getDefinitionId, IDbSession session, ISqlDialect dialect)
{
    /// <summary>The most intervals a range may hold.</summary>
    internal const int IntervalLimit = 60;

    /// <summary>The bins the duration scale has up to the longest duration a worker records.</summary>
    internal const int BinLimit = 94;

    /// <summary>The newest executions a read counts.</summary>
    internal const int ExecutionLimit = 500_000;

    /// <summary>The first two figures a bin above 10 ms starts at, the same in every decade.</summary>
    private static readonly int[] BinSteps = [10, 13, 16, 20, 25, 32, 40, 50, 63, 80];

    /// <summary>
    /// Where each bin starts, ascending, with the scale's end after the last: a bin to each millisecond below 10 ms, then
    /// ten to each decade. Every edge has two significant figures, as every duration group does, so a group falls whole
    /// into one bin.
    /// </summary>
    private static readonly long[] BinEdges =
    [
        .. Enumerable.Range(0, 10).Select(static ms => (long)ms),
        .. Enumerable.Range(0, BinLimit - 9).Select(static i => BinSteps[i % 10] * (long)Math.Pow(10, i / 10)),
    ];

    public async ValueTask<GetDefinitionPerformanceResponse?> HandleAsync(GetDefinitionPerformanceQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Whole milliseconds place an execution in the same interval on every provider: SQLite stores an instant as
        // epoch milliseconds and SQL Server counts millisecond boundaries.
        query = query with
        {
            FromUtc = TruncateToMillisecond(query.FromUtc),
            ToUtc = TruncateToMillisecond(query.ToUtc),
        };
        if (query.ToUtc <= query.FromUtc)
        {
            throw new InvalidQueryException("toUtc must be after fromUtc.");
        }
        if (query.IntervalSeconds < 60)
        {
            throw new InvalidQueryException("intervalSeconds must be at least 60.");
        }
        if (IntervalCount(query) > IntervalLimit)
        {
            throw new InvalidQueryException(
                $"The range holds more than {IntervalLimit} intervals; raise intervalSeconds or shorten the range."
            );
        }

        if (
            await getDefinitionId.HandleAsync(new GetDefinitionIdQuery(query.JobNamespace, query.JobName), ct)
            is not { DefinitionId: var definitionId }
        )
        {
            return null;
        }

        return Map(query, await ReadAsync(definitionId, query, ExecutionLimit, ct), ExecutionLimit);
    }

    /// <summary>The range's newest <paramref name="executionLimit"/> executions, counted per interval, outcome event and duration group.</summary>
    internal async ValueTask<IReadOnlyList<GetDefinitionPerformanceRow>> ReadAsync(
        int definitionId,
        GetDefinitionPerformanceQuery query,
        int executionLimit,
        CancellationToken ct
    ) =>
        await session.QueryListAsync(
            "GetDefinitionPerformance",
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Definition.DefinitionId, definitionId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedFromUtc, query.FromUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.EventCreatedToUtc, query.ToUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.IntervalSeconds, query.IntervalSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.ExecutionFetchLimit, executionLimit));
            },
            DbProjectionResolver.Resolve<GetDefinitionPerformanceRow>(),
            ct
        );

    /// <summary>
    /// Maps the counts to a <see cref="GetDefinitionPerformanceResponse"/>, a pure function. A read that counted
    /// <paramref name="executionLimit"/> executions is taken as cut: the database stops there and does not say whether
    /// an older one exists.
    /// </summary>
    internal static GetDefinitionPerformanceResponse Map(
        GetDefinitionPerformanceQuery query,
        IReadOnlyList<GetDefinitionPerformanceRow> rows,
        int executionLimit
    )
    {
        // job.failed is a failed execution too: the one that ended its job, written in place of job.execution-failed.
        static bool Succeeded(GetDefinitionPerformanceRow row) => row.EventCode == EventCode.JobExecutionSucceeded;

        var byOutcome = rows.ToLookup(Succeeded);
        var byInterval = rows.ToLookup(static row => (row.IntervalNumber, Succeeded(row)));
        var intervals = new GetDefinitionPerformanceInterval[IntervalCount(query)];
        for (var i = 0; i < intervals.Length; i++)
        {
            intervals[i] = new GetDefinitionPerformanceInterval(
                query.FromUtc.AddTicks(i * (query.IntervalSeconds * TimeSpan.TicksPerSecond)),
                Summarize(byInterval[(i, true)]),
                Summarize(byInterval[(i, false)])
            );
        }

        return new GetDefinitionPerformanceResponse(
            query.FromUtc,
            query.ToUtc,
            query.IntervalSeconds,
            rows.Sum(static row => row.ExecutionCount) >= executionLimit,
            rows.Min(static row => (DateTime?)row.OldestAtUtc),
            Summarize(byOutcome[true]),
            Summarize(byOutcome[false]),
            Bins(byOutcome[true]),
            Bins(byOutcome[false]),
            intervals
        );
    }

    private static DateTime TruncateToMillisecond(DateTime value)
    {
        var utc = DbParams.ToUtc(value);
        return utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerMillisecond));
    }

    private static long IntervalCount(GetDefinitionPerformanceQuery query)
    {
        var intervalTicks = query.IntervalSeconds * TimeSpan.TicksPerSecond;
        return ((query.ToUtc - query.FromUtc).Ticks + intervalTicks - 1) / intervalTicks;
    }

    /// <summary>
    /// The count and nearest-rank percentiles of the executions <paramref name="rows"/> count. A rank is taken in whole
    /// numbers, so no rounding of a fraction decides which execution a percentile names.
    /// </summary>
    private static GetDefinitionPerformanceExecutions Summarize(IEnumerable<GetDefinitionPerformanceRow> rows)
    {
        GetDefinitionPerformanceRow[] shortestFirst = [.. rows.OrderBy(static row => row.DurationGroupMs)];
        var count = shortestFirst.Sum(static row => row.ExecutionCount);
        var (rank50, rank95, rank99) = ((50 * count + 99) / 100, (95 * count + 99) / 100, (99 * count + 99) / 100);
        int? p50 = null;
        int? p95 = null;
        int? p99 = null;
        var reached = 0L;
        foreach (var row in shortestFirst)
        {
            reached += row.ExecutionCount;
            p50 ??= reached >= rank50 ? row.DurationGroupMs : (int?)null;
            p95 ??= reached >= rank95 ? row.DurationGroupMs : (int?)null;
            p99 ??= reached >= rank99 ? row.DurationGroupMs : (int?)null;
        }

        return new GetDefinitionPerformanceExecutions(count, p50, p95, p99);
    }

    /// <summary>The histogram of the executions <paramref name="rows"/> count, from the first bin that holds one to the last.</summary>
    private static GetDefinitionPerformanceBin[] Bins(IEnumerable<GetDefinitionPerformanceRow> rows)
    {
        var counts = new long[BinLimit];
        foreach (var row in rows)
        {
            // A duration that is no edge falls in the bin the edge below it starts.
            var index = Array.BinarySearch(BinEdges, (long)row.DurationGroupMs);
            counts[index >= 0 ? index : ~index - 1] += row.ExecutionCount;
        }

        var first = Array.FindIndex(counts, static count => count > 0);
        if (first < 0)
        {
            return [];
        }

        var bins = new GetDefinitionPerformanceBin[Array.FindLastIndex(counts, static count => count > 0) - first + 1];
        for (var i = 0; i < bins.Length; i++)
        {
            bins[i] = new GetDefinitionPerformanceBin(BinEdges[first + i], BinEdges[first + i + 1], counts[first + i]);
        }

        return bins;
    }
}
