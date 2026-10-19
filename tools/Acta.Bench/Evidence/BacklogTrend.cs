namespace Acta.Bench;

public readonly record struct BacklogSample(long Timestamp, int Backlog);

public static class BacklogTrend
{
    // Sample every observer cycle, then average one-second blocks. Sampling only once
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
