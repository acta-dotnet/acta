namespace Acta.Bench;

public sealed record HistogramSnapshot(
    IReadOnlyList<long> Bins,
    long Count,
    long Overflow,
    double MaximumMs,
    string Unit = "milliseconds",
    int BinsPerOctave = 128
);

public sealed class LatencyHistogram
{
    private readonly long[] _bins = new long[4096];
    private long _count;
    private long _overflow;
    private double _maximum;

    public void Record(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        {
            _overflow++;
            return;
        }
        var index = (int)Math.Ceiling(Math.Log2(1 + milliseconds * 1000) * 128);
        if (index >= _bins.Length)
        {
            _overflow++;
            return;
        }
        _bins[index]++;
        _count++;
        _maximum = Math.Max(_maximum, milliseconds);
    }

    public double? Percentile(double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percentile, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 1);
        if (_count == 0)
        {
            return null;
        }
        var target = Math.Max(1, (long)Math.Ceiling(_count * percentile));
        long accumulated = 0;
        for (var i = 0; i < _bins.Length; i++)
        {
            accumulated += _bins[i];
            if (accumulated >= target)
            {
                return Math.Min(_maximum, (Math.Pow(2, i / 128.0) - 1) / 1000);
            }
        }
        return _maximum;
    }

    /// <summary>The bins up to the highest occupied one; every bin past it is empty.</summary>
    public HistogramSnapshot Snapshot() =>
        new([.. _bins.AsSpan(0, Array.FindLastIndex(_bins, b => b > 0) + 1)], _count, _overflow, _maximum);
}
