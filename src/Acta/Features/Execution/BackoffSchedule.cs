namespace Acta.Features.Execution;

/// <summary>
/// Computes the retry backoff delay for a failed attempt: <c>initial * multiplier^(attempt-1)</c>,
/// capped at the maximum, with symmetric jitter applied. The single origin of the retry growth curve so
/// the policy lives in one place.
/// </summary>
internal static class BackoffSchedule
{
    /// <summary>
    /// Whole-second delay for retry <paramref name="attemptNumber"/> (1 = the first retry, applying
    /// the initial delay). Jitter is a symmetric fraction in <c>[-jitter, +jitter]</c> of the capped
    /// delay; the result is clamped to <c>[0, maxSeconds]</c>.
    /// </summary>
    public static int ComputeDelaySeconds(int attemptNumber, int initialSeconds, int maxSeconds, decimal multiplier, decimal jitter)
    {
        if (attemptNumber < 1)
        {
            attemptNumber = 1;
        }

        var growth = Math.Pow((double)multiplier, attemptNumber - 1);
        var delay = Math.Min(initialSeconds * growth, maxSeconds);

        var jitterFraction = (double)jitter;
        if (jitterFraction > 0)
        {
            var swing = delay * jitterFraction;
            delay += (Random.Shared.NextDouble() * 2 - 1) * swing;
        }

        if (delay < 0)
        {
            delay = 0;
        }
        else if (delay > maxSeconds)
        {
            delay = maxSeconds;
        }

        return (int)Math.Round(delay);
    }

    /// <summary>
    /// Whole-second delay for retry <paramref name="attemptNumber"/>, from a parsed <see cref="Acta.Backoff"/>.
    /// </summary>
    public static int ComputeDelaySeconds(int attemptNumber, Backoff backoff) =>
        ComputeDelaySeconds(
            attemptNumber,
            (int)backoff.InitialDelay.TotalSeconds,
            (int)backoff.MaxDelay.TotalSeconds,
            (decimal)backoff.Multiplier,
            (decimal)backoff.Jitter
        );
}
