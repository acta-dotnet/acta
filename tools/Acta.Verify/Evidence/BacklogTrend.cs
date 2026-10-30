namespace Acta.Verify;

public readonly record struct BacklogSample(long Timestamp, int Backlog);

public static class BacklogTrend
{
    /// <summary>
    /// The backlog every <paramref name="step"/> ticks from <paramref name="from"/> to <paramref name="to"/>: the arrivals
    /// <paramref name="scheduledBy"/> each instant, less the completions the cumulative curve <paramref name="completed"/>
    /// had reached by it. An arrival counts from its scheduled instant, so a stream that fell behind its schedule stays in
    /// the backlog, and one that never completed stays counted.
    /// </summary>
    public static BacklogSample[] Reconstruct(
        Func<long, long> scheduledBy,
        IReadOnlyList<DrainSample> completed,
        long from,
        long to,
        long step
    )
    {
        ArgumentNullException.ThrowIfNull(scheduledBy);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        List<BacklogSample> samples = [];
        var (next, done) = (0, 0L);
        for (var t = from; t <= to; t += step)
        {
            for (; next < completed.Count && completed[next].Timestamp <= t; next++)
            {
                done = completed[next].Completed;
            }
            samples.Add(new(t, (int)(scheduledBy(t) - done)));
        }
        return [.. samples];
    }

    // Sample several times a second, then average one-second blocks. Sampling only once
    // per second aliases the worker's one-second safety poll into a false queue trend.
    public static double? Slope(IReadOnlyList<BacklogSample> samples, long clockFrequency)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockFrequency);
        if (samples.Count == 0)
            return null;
        var origin = samples[0].Timestamp;
        var blocks = samples
            .GroupBy(p => (p.Timestamp - origin) / clockFrequency)
            .Where(g => g.Max(p => p.Timestamp) - g.Min(p => p.Timestamp) >= clockFrequency / 2)
            .Select(g => (Time: g.Average(p => (p.Timestamp - origin) / (double)clockFrequency), Backlog: g.Average(p => p.Backlog)))
            .ToArray();
        if (blocks.Length < 2)
            return null;
        var meanX = blocks.Average(p => p.Time);
        var meanY = blocks.Average(p => p.Backlog);
        var denominator = blocks.Sum(p => Math.Pow(p.Time - meanX, 2));
        return denominator == 0 ? null : blocks.Sum(p => (p.Time - meanX) * (p.Backlog - meanY)) / denominator;
    }
}
