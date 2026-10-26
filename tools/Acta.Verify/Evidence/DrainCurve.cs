namespace Acta.Verify;

/// <summary>One count of a fixed cohort's durably terminal roots, stamped when the count returned, with the count's own duration.</summary>
public readonly record struct DrainSample(long Timestamp, long Completed, long QueryTicks = 0);

/// <summary>Instants and rates read off a fixed-cohort drain's sampled terminal counts.</summary>
public static class DrainCurve
{
    /// <summary>The first sampled instant at which the count had reached <paramref name="target"/>; null if none had.</summary>
    public static long? Reached(IReadOnlyList<DrainSample> samples, long target) =>
        samples.Where(s => s.Completed >= target).Select(s => (long?)s.Timestamp).FirstOrDefault();

    /// <summary>
    /// The instant the count crossed <paramref name="target"/>, interpolated linearly between the samples either side;
    /// null when no sample reached it, or the first one did and leaves nothing to interpolate from.
    /// </summary>
    public static double? Crossing(IReadOnlyList<DrainSample> samples, double target)
    {
        ArgumentNullException.ThrowIfNull(samples);
        for (var i = 1; i < samples.Count; i++)
        {
            var (before, after) = (samples[i - 1], samples[i]);
            if (before.Completed < target && after.Completed >= target)
            {
                return before.Timestamp
                    + (after.Timestamp - before.Timestamp) * ((target - before.Completed) / (after.Completed - before.Completed));
            }
        }
        return null;
    }

    /// <summary>Roots a second between the cohort's 20% and 80% crossings.</summary>
    public static double? SteadyRate(IReadOnlyList<DrainSample> samples, long cohort, long clockFrequency) =>
        Crossing(samples, 0.2 * cohort) is { } lower && Crossing(samples, 0.8 * cohort) is { } upper && upper > lower
            ? 0.6 * cohort * clockFrequency / (upper - lower)
            : null;

    /// <summary>The 0-based ascending rank of <paramref name="count"/> values' <paramref name="percent"/>th percentile, as percentile_disc picks it.</summary>
    public static long PercentileRank(long count, int percent) => (count * percent + 99) / 100 - 1;
}
