namespace Acta.Bench;

public readonly record struct BacklogSample(long Timestamp, int Backlog);

public static class BacklogTrend
{
    /// <summary>
    /// The backlog every <paramref name="step"/> ticks from <paramref name="from"/> to <paramref name="to"/>: the roots
    /// acknowledged and not yet completed at each instant. A root completed before its acknowledgement returned never
    /// counts, and one never completed stays counted.
    /// </summary>
    public static BacklogSample[] Reconstruct(IEnumerable<(long Acknowledged, long Completed)> roots, long from, long to, long step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        var all = roots.ToArray();
        var opened = all.Select(r => r.Acknowledged).Order().ToArray();
        var closed = all.Select(r => Math.Max(r.Acknowledged, r.Completed)).Order().ToArray();
        List<BacklogSample> samples = [];
        var (open, close) = (0, 0);
        for (var t = from; t <= to; t += step)
        {
            while (open < opened.Length && opened[open] <= t)
            {
                open++;
            }
            while (close < closed.Length && closed[close] <= t)
            {
                close++;
            }
            samples.Add(new(t, open - close));
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
