using System.Globalization;
using System.Xml;

namespace Acta;

/// <summary>
/// Normalizes handler-supplied delays to the whole-second precision the reschedule / sleep
/// surface persists (the <c>delay_seconds</c> routine argument is an <c>INT</c>).
/// </summary>
internal static class DurationSyntax
{
    /// <summary>Parses Acta's canonical human duration syntax: number + ms/s/m/h/d.</summary>
    public static TimeSpan ParseHuman(string text)
    {
        var s = text.Trim();
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
        {
            i++;
        }

        if (i == 0 || i == s.Length || s[0] == '.' || s[i - 1] == '.')
        {
            throw new FormatException($"'{text}' is not a valid duration.");
        }

        var number = double.Parse(s[..i], CultureInfo.InvariantCulture);
        try
        {
            return s[i..] switch
            {
                "ms" => TimeSpan.FromMilliseconds(number),
                "s" => TimeSpan.FromSeconds(number),
                "m" => TimeSpan.FromMinutes(number),
                "h" => TimeSpan.FromHours(number),
                "d" => TimeSpan.FromDays(number),
                var unit => throw new FormatException(
                    $"Unit '{unit}' is not valid. Use '1m' for minutes. (Acta durations have no calendar units.)"
                ),
            };
        }
        catch (OverflowException)
        {
            // Every caller (Backoff.Parse/TryParse) treats a malformed duration as a FormatException, not
            // an OverflowException; a huge numeral like "99999999999999999999d" must fail the same way
            // instead of leaking a distinct exception type callers don't expect.
            throw new FormatException($"'{text}' is not a valid duration.");
        }
    }

    /// <summary>Parses a duration: the human syntax (<c>1m</c>) or its ISO-8601 time-only equivalent (<c>PT1M</c>).</summary>
    public static TimeSpan ParseDuration(string text)
    {
        var s = text.Trim();
        if (s.Length == 0)
        {
            throw new FormatException("Duration must not be blank.");
        }

        if (s[0] is 'P' or 'p')
        {
            return !IsoIsTimeOnly(s)
                ? throw new FormatException("Acta durations do not allow calendar ISO-8601 units.")
                : XmlConvert.ToTimeSpan(s);
        }

        return ParseHuman(s);
    }

    /// <summary>
    /// Converts <paramref name="delay"/> to whole seconds: a sub-second positive delay rounds up so a
    /// caller-positive delay never collapses to "run immediately"; zero stays zero; a negative delay
    /// or one beyond the <c>INT</c> seconds ceiling is a programming error and throws.
    /// </summary>
    public static int ToWholeSeconds(TimeSpan delay, string paramName)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, delay, "Delay must not be negative.");
        }

        var seconds = Math.Ceiling(delay.TotalSeconds);
        return seconds > int.MaxValue
            ? throw new ArgumentOutOfRangeException(paramName, delay, $"Delay must not exceed {int.MaxValue} seconds.")
            : (int)seconds;
    }

    private static bool IsoIsTimeOnly(string s)
    {
        var t = s.IndexOf('T');
        if (t < 0)
        {
            return false;
        }

        for (var i = 1; i < t; i++)
        {
            if (!char.IsDigit(s[i]) && s[i] != '.' && s[i] != '+' && s[i] != '-')
            {
                return false;
            }
        }
        return true;
    }
}
