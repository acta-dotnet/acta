namespace Acta.Verify;

/// <summary>One count of a cohort's durably terminal roots at an instant, with the count's own duration where a query took it.</summary>
public readonly record struct DrainSample(long Timestamp, long Completed, long QueryTicks = 0);

/// <summary>Instants and rates read off a cumulative curve of terminal counts.</summary>
public static class DrainCurve
{
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
}
