using System.Globalization;

namespace Acta;

/// <summary>
/// A parsed rate-limit declaration: <c>N/s</c>, <c>N/m</c>, or <c>N/h</c> resolved to the pair the
/// engine meters with, a whole-millisecond emission interval and a burst of N. The interval is the
/// period divided by N and rounded UP, so the realized rate is at or just under the declared one
/// rather than over it. The one parser for the format: the generator diagnostic, registration, the
/// operator override gate, and admission all read a rate through here.
/// </summary>
internal readonly record struct RateLimitSpec
{
    private RateLimitSpec(int count, char period, int intervalMilliseconds)
    {
        Count = count;
        Period = period;
        IntervalMilliseconds = intervalMilliseconds;
    }

    /// <summary>The declared N: how many admissions the bucket may hand out back to back.</summary>
    public int Count { get; }

    /// <summary>The declared period, one of <c>s</c>, <c>m</c>, <c>h</c>.</summary>
    public char Period { get; }

    /// <summary>Whole milliseconds between two admissions; at least 1.</summary>
    public int IntervalMilliseconds { get; }

    /// <summary>Canonical text (<c>"{Count}/{Period}"</c>), the form stored and compared on a shared key.</summary>
    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Count}/{Period}");

    /// <summary>
    /// The longest rate a millisecond-resolution store can meter: an interval of one millisecond.
    /// A faster declaration is rejected rather than silently rounded down to this.
    /// </summary>
    public const int MaxCountPerSecond = 1000;

    /// <summary>Longest accepted declaration, matching <c>definitions.rate_limit</c>.</summary>
    public const int MaxTextLength = 16;

    /// <summary>
    /// Parses a declaration, or explains in <paramref name="error"/> why it is not one. The message is
    /// the operator- and developer-facing sentence every gate reports, so it never mentions a caller.
    /// </summary>
    public static bool TryParse(string? text, out RateLimitSpec spec, out string? error)
    {
        spec = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "A rate limit is a count, a slash, and a period: \"10/s\", \"600/m\", or \"5000/h\".";
            return false;
        }
        if (text.Length > MaxTextLength)
        {
            error = $"A rate limit is at most {MaxTextLength} characters.";
            return false;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash != text.Length - 2)
        {
            error = "A rate limit is a count, a slash, and a single period letter: \"10/s\", \"600/m\", or \"5000/h\".";
            return false;
        }

        var period = text[^1];
        var periodMilliseconds = period switch
        {
            's' => 1_000,
            'm' => 60_000,
            'h' => 3_600_000,
            _ => 0,
        };
        if (periodMilliseconds == 0)
        {
            error = "A rate limit's period is s, m, or h.";
            return false;
        }

        if (!int.TryParse(text.AsSpan(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
        {
            error = "A rate limit's count is a positive whole number.";
            return false;
        }

        // The ceiling is one admission per millisecond, whatever the period: SQL Server and SQLite
        // store the arrival time to the millisecond, so a shorter interval would not move the bucket.
        var maxCount = periodMilliseconds;
        if (count > maxCount)
        {
            error =
                $"A rate limit of {count}/{period} is faster than {MaxCountPerSecond} per second; "
                + $"the ceiling for this period is {maxCount}/{period}.";
            return false;
        }

        // Rounded up so the interval is never zero and the realized rate never exceeds the declared one.
        var interval = (periodMilliseconds + count - 1) / count;
        spec = new RateLimitSpec(count, period, interval);
        return true;
    }

    /// <summary>Parses a declaration already proven valid by a gate; throws when it is not.</summary>
    public static RateLimitSpec Parse(string text) =>
        TryParse(text, out var spec, out var error) ? spec : throw new ArgumentException(error, nameof(text));
}
