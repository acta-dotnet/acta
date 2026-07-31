using System.Globalization;

namespace Acta;

/// <summary>
/// Typed retry-backoff policy: a capped exponential curve with symmetric jitter, the single growth
/// shape the engine applies. It is a constructor for the four persisted scalars (initial delay, max
/// delay, multiplier, jitter), not a runtime evaluator; the delay math lives in one place on the
/// engine side. Build one with <see cref="Fixed"/> or <see cref="Exponential"/> and hand it to a
/// retry-override surface such as <c>StepOptionsBuilder.WithPolicy</c>.
/// </summary>
public readonly record struct Backoff
{
    private Backoff(TimeSpan initialDelay, TimeSpan maxDelay, double multiplier, double jitter)
    {
        InitialDelay = initialDelay;
        MaxDelay = maxDelay;
        Multiplier = multiplier;
        Jitter = jitter;
    }

    /// <summary>Delay before the first retry; the curve grows from here.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>Ceiling the grown delay is clamped to.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>Per-attempt growth factor; 1.0 is a flat (fixed) curve.</summary>
    public double Multiplier { get; }

    /// <summary>Symmetric jitter fraction in [0, 1] applied to the capped delay.</summary>
    public double Jitter { get; }

    /// <summary>
    /// Framework default: 1 minute growing to 8 hours, doubling each attempt, with 10% jitter.
    /// </summary>
    public static Backoff Default => Range(TimeSpan.FromMinutes(1), TimeSpan.FromHours(8));

    /// <summary>Exponential growth from <paramref name="initial"/> to <paramref name="max"/>, doubling, with 10% jitter.</summary>
    public static Backoff Range(TimeSpan initial, TimeSpan max) => Exponential(initial, max).WithJitter(0.1);

    /// <summary>
    /// Constant <paramref name="delay"/> before every retry (multiplier 1.0, no growth).
    /// </summary>
    public static Backoff Fixed(TimeSpan delay)
    {
        return delay < TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must not be negative.")
            : new Backoff(delay, delay, multiplier: 1.0, jitter: 0.0);
    }

    /// <summary>
    /// Exponential growth from <paramref name="initial"/>, clamped at <paramref name="max"/>, scaling
    /// by <paramref name="multiplier"/> per failed attempt.
    /// </summary>
    public static Backoff Exponential(TimeSpan initial, TimeSpan max, double multiplier = 2.0)
    {
        if (initial < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initial), initial, "Initial delay must not be negative.");
        }
        if (max < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(max), max, "Max delay must not be less than the initial delay.");
        }
        return !double.IsFinite(multiplier) || multiplier < 1.0
            ? throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "Multiplier must be a finite number of at least 1.0.")
            : new Backoff(initial, max, multiplier, jitter: 0.0);
    }

    /// <summary>
    /// Returns a copy with the symmetric jitter fraction set; must be in [0, 1].
    /// </summary>
    public Backoff WithJitter(double fraction)
    {
        return !double.IsFinite(fraction) || fraction is < 0.0 or > 1.0
            ? throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "Jitter must be in [0, 1].")
            : new Backoff(InitialDelay, MaxDelay, Multiplier, fraction);
    }

    /// <summary>Parses Acta's backoff DSL, e.g. <c>1m..8h x2 +-10%</c>.</summary>
    public static Backoff Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var parts = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("Backoff expression must not be blank.");
        }

        var range = parts[0];
        var dots = range.IndexOf("..", StringComparison.Ordinal);
        var ranged = dots >= 0;
        var initial = DurationSyntax.ParseDuration(ranged ? range[..dots] : range);
        var max = ranged ? DurationSyntax.ParseDuration(range[(dots + 2)..]) : initial;
        if (max < initial)
        {
            throw new ArgumentOutOfRangeException(nameof(expression), expression, "Max delay must not be less than the initial delay.");
        }

        var multiplier = ranged ? 2.0 : 1.0;
        var jitter = ranged ? 0.1 : 0.0;

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part == "exact")
            {
                jitter = 0.0;
            }
            else if (part.StartsWith('x'))
            {
                multiplier = ParsePositiveNumber(part[1..], nameof(multiplier));
            }
            else
            {
                jitter = ParseJitter(part);
            }
        }

        return new Backoff(initial, max, multiplier, jitter);
    }

    /// <summary>Tries to parse Acta's backoff DSL.</summary>
    public static bool TryParse(string? expression, out Backoff backoff)
    {
        backoff = default;
        if (expression is null)
        {
            return false;
        }

        try
        {
            backoff = Parse(expression);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public override string ToString()
    {
        var ranged = MaxDelay != InitialDelay;
        var text = ranged ? $"{FormatDuration(InitialDelay)}..{FormatDuration(MaxDelay)}" : FormatDuration(InitialDelay);
        var defaultMultiplier = ranged ? 2.0 : 1.0;
        if (Multiplier != defaultMultiplier || ranged)
        {
            text += $" x{FormatNumber(Multiplier)}";
        }
        if (Jitter > 0)
        {
            text += $" ±{FormatNumber(Jitter * 100)}%";
        }
        else if (ranged)
        {
            text += " exact";
        }
        return text;
    }

    private static double ParsePositiveNumber(string text, string name)
    {
        return !double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 1.0
            ? throw new FormatException($"{name} must be at least 1.0.")
            : number;
    }

    private static double ParseJitter(string text)
    {
        string number;
        if (text.StartsWith('±'))
        {
            number = text[1..];
        }
        else if (text.StartsWith("+-", StringComparison.Ordinal))
        {
            number = text[2..];
        }
        else
        {
            number = text.StartsWith('~') ? text[1..] : throw new FormatException($"'{text}' is not a valid backoff clause.");
        }

        if (!number.EndsWith('%'))
        {
            throw new FormatException("Jitter must be a percentage.");
        }

        if (!double.TryParse(number[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percent))
        {
            throw new FormatException("Jitter must be a percentage.");
        }

        return percent is < 0.0 or > 100.0 ? throw new FormatException("Jitter must be between 0% and 100%.") : percent / 100.0;
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value == TimeSpan.Zero)
        {
            return "0s";
        }
        if (value.Ticks % TimeSpan.TicksPerDay == 0)
        {
            return FormatNumber(value.TotalDays) + "d";
        }
        if (value.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return FormatNumber(value.TotalHours) + "h";
        }
        if (value.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return FormatNumber(value.TotalMinutes) + "m";
        }
        if (value.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            return FormatNumber(value.TotalSeconds) + "s";
        }
        return value.Ticks % TimeSpan.TicksPerMillisecond == 0
            ? FormatNumber(value.TotalMilliseconds) + "ms"
            : FormatNumber(value.TotalSeconds) + "s";
    }

    private static string FormatNumber(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
