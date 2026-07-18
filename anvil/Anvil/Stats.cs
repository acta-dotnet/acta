using System.Diagnostics;

namespace Anvil;

/// <summary>
/// Percentile helpers over Stopwatch-tick latency samples, for the dashboard's live latency readout.
/// (Anvil.Bench carries its own copy for the headless measurement path; the two are independent by design.)
/// </summary>
public static class Stats
{
    /// <summary>Converts Stopwatch ticks to milliseconds.</summary>
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Returns p50, p95, p99, max and mean (all milliseconds) over the given tick samples. An empty
    /// input yields all zeros.
    /// </summary>
    public static (double P50, double P95, double P99, double Max, double Mean) Percentiles(long[] ticks)
    {
        if (ticks.Length == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        Array.Sort(ticks);
        double At(double q) => Ms(ticks[Math.Clamp((int)(q * ticks.Length), 0, ticks.Length - 1)]);

        double sum = 0;
        foreach (var t in ticks)
        {
            sum += Ms(t);
        }

        return (At(0.50), At(0.95), At(0.99), Ms(ticks[^1]), sum / ticks.Length);
    }
}
