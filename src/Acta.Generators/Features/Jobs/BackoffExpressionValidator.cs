using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace Acta.Generators.Features.Jobs;

internal static class BackoffExpressionValidator
{
    public enum ErrorKind
    {
        Invalid,
        InvalidUnit,
    }

    public readonly record struct Error(ErrorKind Kind, string Value, string? Unit = null);

    public readonly record struct ParsedBackoff(int InitialSeconds, int MaxSeconds, decimal Multiplier, decimal Jitter);

    public static bool TryParseBackoff(
        string expression,
        List<(string Iso, string Human)> legacyIso,
        out ParsedBackoff backoff,
        out Error error
    )
    {
        backoff = default;
        error = default;
        var parts = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = new Error(ErrorKind.Invalid, expression);
            return false;
        }

        var range = parts[0];
        var dots = range.IndexOf("..", StringComparison.Ordinal);
        var ranged = dots >= 0;
        if (
            !TryParseDuration(ranged ? range.Substring(0, dots) : range, legacyIso, out var initial, out error)
            || !TryParseDuration(ranged ? range.Substring(dots + 2) : range, legacyIso, out var max, out error)
            || max < initial
        )
        {
            return false;
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
            else if (part.StartsWith("x", StringComparison.Ordinal))
            {
                if (!TryNumber(part.Substring(1), out multiplier) || multiplier < 1.0 || multiplier > 99999.9999)
                {
                    error = new Error(ErrorKind.Invalid, part);
                    return false;
                }
            }
            else if (!TryJitter(part, out jitter))
            {
                error = new Error(ErrorKind.Invalid, part);
                return false;
            }
        }

        if (!TryToWholeSeconds(initial, out var initialSeconds) || !TryToWholeSeconds(max, out var maxSeconds))
        {
            error = new Error(ErrorKind.Invalid, expression);
            return false;
        }

        backoff = new ParsedBackoff(initialSeconds, maxSeconds, (decimal)multiplier, (decimal)jitter);
        return true;
    }

    public static bool TryParseDuration(string text, List<(string Iso, string Human)> legacyIso, out TimeSpan span, out Error error)
    {
        var s = text.Trim();
        span = default;
        error = default;
        if (s.Length == 0)
        {
            error = new Error(ErrorKind.Invalid, text);
            return false;
        }

        if (s[0] is 'P' or 'p')
        {
            try
            {
                if (!IsoIsTimeOnly(s))
                {
                    throw new FormatException();
                }
                span = XmlConvert.ToTimeSpan(s);
                if (span < TimeSpan.Zero)
                {
                    error = new Error(ErrorKind.Invalid, text);
                    return false;
                }
                legacyIso.Add((s, FormatDuration(span)));
                return true;
            }
            catch (FormatException)
            {
                error = new Error(ErrorKind.Invalid, text);
                return false;
            }
        }

        return TryParseHuman(s, out span, out error);
    }

    public static bool TryToWholeSeconds(TimeSpan span, out int seconds)
    {
        seconds = 0;
        if (span < TimeSpan.Zero)
        {
            return false;
        }

        var whole = Math.Ceiling(span.TotalSeconds);
        if (whole > int.MaxValue)
        {
            return false;
        }

        seconds = (int)whole;
        return true;
    }

    public static string FormatDuration(TimeSpan value)
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
        if (value.Ticks % TimeSpan.TicksPerMillisecond == 0)
        {
            return FormatNumber(value.TotalMilliseconds) + "ms";
        }
        return FormatNumber(value.TotalSeconds) + "s";
    }

    private static bool TryParseHuman(string s, out TimeSpan span, out Error error)
    {
        span = default;
        error = default;
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
        {
            i++;
        }

        if (i == 0 || i == s.Length || s[0] == '.' || s[i - 1] == '.' || !TryNumber(s.Substring(0, i), out var number))
        {
            error = new Error(ErrorKind.Invalid, s);
            return false;
        }

        try
        {
            switch (s.Substring(i))
            {
                case "ms":
                    span = TimeSpan.FromMilliseconds(number);
                    return true;
                case "s":
                    span = TimeSpan.FromSeconds(number);
                    return true;
                case "m":
                    span = TimeSpan.FromMinutes(number);
                    return true;
                case "h":
                    span = TimeSpan.FromHours(number);
                    return true;
                case "d":
                    span = TimeSpan.FromDays(number);
                    return true;
                case var unit:
                    error = new Error(IsCalendarOrUpperUnit(unit) ? ErrorKind.InvalidUnit : ErrorKind.Invalid, s, unit);
                    return false;
            }
        }
        catch (OverflowException)
        {
            // A huge numeral (e.g. "99999999999999999999d") must yield an ACTA0105 diagnostic, not crash
            // the source generator.
            span = default;
            error = new Error(ErrorKind.Invalid, s);
            return false;
        }
    }

    private static bool TryJitter(string text, out double jitter)
    {
        jitter = 0;
        string number;
        if (text.StartsWith("±", StringComparison.Ordinal))
        {
            number = text.Substring(1);
        }
        else if (text.StartsWith("+-", StringComparison.Ordinal))
        {
            number = text.Substring(2);
        }
        else if (text.StartsWith("~", StringComparison.Ordinal))
        {
            number = text.Substring(1);
        }
        else
        {
            return false;
        }

        if (
            !number.EndsWith("%", StringComparison.Ordinal)
            || !TryNumber(number.Substring(0, number.Length - 1), out var percent)
            || percent < 0.0
            || percent > 100.0
        )
        {
            return false;
        }

        jitter = percent / 100.0;
        return true;
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

    private static string FormatNumber(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool IsCalendarOrUpperUnit(string unit)
    {
        if (unit is "M" or "D" or "Y" or "w")
        {
            return true;
        }
        foreach (var c in unit)
        {
            if (char.IsUpper(c))
            {
                return true;
            }
        }
        return false;
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
