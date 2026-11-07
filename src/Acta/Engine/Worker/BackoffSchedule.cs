namespace Acta;

/// <summary>
/// Computes the retry backoff delay for a failed attempt: <c>initial * multiplier^(attempt-1)</c>,
/// capped at the maximum, with symmetric jitter applied. The single origin of the retry growth curve.
/// </summary>
internal static class BackoffSchedule
{
    /// <summary>
    /// Whole-second delay for retry <paramref name="attemptNumber"/> (1 = the first retry, applying
    /// the initial delay). Jitter is a symmetric fraction in <c>[-jitter, +jitter]</c> of the capped
    /// delay; the result is clamped to <c>[0, max]</c>.
    /// </summary>
    public static int ComputeDelaySeconds(int attemptNumber, Backoff backoff)
    {
        var initialSeconds = DurationSyntax.ToWholeSeconds(backoff.InitialDelay, nameof(backoff));
        var maxSeconds = DurationSyntax.ToWholeSeconds(backoff.MaxDelay, nameof(backoff));
        if (attemptNumber < 1)
        {
            attemptNumber = 1;
        }

        var growth = Math.Pow(backoff.Multiplier, attemptNumber - 1);
        var delay = Math.Min(initialSeconds * growth, maxSeconds);

        if (backoff.Jitter > 0)
        {
            var swing = delay * backoff.Jitter;
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
}
