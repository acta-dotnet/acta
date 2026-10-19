using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>Parses and checks the single policy values a <c>[Job]</c> attribute names: lanes, rates, backoff, durations, codes.</summary>
internal static class JobPolicyValues
{
    /// <summary>
    /// The name rule the runtime enforces on a lane in <c>NameSyntax.Canonicalize</c>: trimmed and lowercased,
    /// dotted kebab of at most 128 characters, and not the reserved <c>sys</c> or under <c>sys.</c>. The
    /// descriptor carries the canonical form, the same value registration would store.
    /// </summary>
    internal static bool TryCanonicalizeLane(string lane, out string canonical)
    {
        canonical = lane.Trim().ToLowerInvariant();
        return KebabName.IsValid(canonical, maxLength: 128);
    }

    /// <summary>
    /// Validates the raw `RateLimit` string against the same format `RateLimitSpec` parses at runtime,
    /// so a typo is a build error rather than a definition that quietly meters nothing. The rate is
    /// carried forward as written; registration stores its canonical form.
    /// </summary>
    internal static string? ReadRateLimit(KeyValuePair<string, TypedConstant> named, List<DiagnosticRecord> diagnostics, Location location)
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var slash = text.IndexOf('/');
        var periodMilliseconds = slash > 0 && slash == text.Length - 2 ? PeriodMilliseconds(text[text.Length - 1]) : 0;
        if (
            periodMilliseconds != 0
            && int.TryParse(text.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count >= 1
            && count <= periodMilliseconds
            && text[0] != '0'
        )
        {
            return text;
        }

        diagnostics.Add(
            ManifestDiagnostics.InvalidPolicyValue(
                named.Key,
                $"\"{text}\"",
                "A rate limit is a positive count with no leading zero, a slash, and a period of s, m, or h (\"10/s\"), at most 1000 per second.",
                location
            )
        );
        return null;
    }

    internal static int PeriodMilliseconds(char period) =>
        period switch
        {
            's' => 1_000,
            'm' => 60_000,
            'h' => 3_600_000,
            _ => 0,
        };

    /// <summary>
    /// Validates the raw `Backoff` DSL string (format + the 64-char storage ceiling) but carries the
    /// RAW text forward - the definitions column stores the expression itself, never the parsed knobs.
    /// </summary>
    internal static string? ReadBackoff(KeyValuePair<string, TypedConstant> named, List<DiagnosticRecord> diagnostics, Location location)
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!BackoffExpressionValidator.TryParseBackoff(text, out var error))
        {
            ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
            return null;
        }

        if (text.Length > 64)
        {
            diagnostics.Add(
                ManifestDiagnostics.InvalidPolicyValue(named.Key, $"\"{text}\"", "Backoff expressions are at most 64 characters.", location)
            );
            return null;
        }

        return text;
    }

    /// <summary>
    /// [Job] durations accept both the human syntax (e.g. "1m") and its ISO-8601 time-only
    /// equivalent (e.g. "PT1M"). The descriptor and DB carry whole seconds.
    /// </summary>
    internal static int? ReadDurationSeconds(
        KeyValuePair<string, TypedConstant> named,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        if (named.Value.Value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (
            BackoffExpressionValidator.TryParseDuration(text, out var span, out var error)
            && BackoffExpressionValidator.TryToWholeSeconds(span, out var seconds)
        )
        {
            return seconds;
        }

        ReportDurationSyntaxError(named.Key, text, error, diagnostics, location);
        return null;
    }

    internal static void ReportDurationSyntaxError(
        string argument,
        string value,
        BackoffExpressionValidator.Error error,
        List<DiagnosticRecord> diagnostics,
        Location location
    )
    {
        diagnostics.Add(
            error.Kind == BackoffExpressionValidator.ErrorKind.InvalidUnit
                ? ManifestDiagnostics.InvalidDurationUnit(error.Unit ?? value, location)
                : ManifestDiagnostics.InvalidDuration(argument, value, location)
        );
    }

    /// <summary>
    /// Enum-typed attribute arguments box as their underlying type: byte for the code-family enums
    /// (JobPriorityCode / DefinitionAuditLevelCode / AlertProfileCode), so an `is int` pattern silently
    /// misses them. Read whatever integral the constant carries and narrow to byte.
    /// </summary>
    internal static bool TryGetEnumByte(TypedConstant constant, out byte value)
    {
        value = 0;
        if (constant.IsNull || constant.Value is null)
        {
            return false;
        }
        try
        {
            value = Convert.ToByte(constant.Value, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            return false;
        }
    }

    internal static string? PriorityIdToName(byte id) =>
        id switch
        {
            0 => "Bulk",
            50 => "Normal",
            70 => "High",
            85 => "Critical",
            100 => "Realtime",
            _ => null,
        };

    internal static string? AuditLevelIdToName(byte id) =>
        id switch
        {
            0 => "Off",
            10 => "Failures",
            20 => "Audit",
            _ => null,
        };

    internal static string? AlertProfileIdToName(byte id) =>
        id switch
        {
            0 => "None",
            10 => "OnFailure",
            20 => "Info",
            30 => "OnTerminal",
            40 => "SysCritical",
            _ => null,
        };

    internal static string? TenantRequirementIdToName(byte id) =>
        id switch
        {
            0 => "Optional",
            10 => "Required",
            20 => "Forbidden",
            _ => null,
        };
}

internal static class BackoffExpressionValidator
{
    public enum ErrorKind
    {
        Invalid,
        InvalidUnit,
    }

    public readonly record struct Error(ErrorKind Kind, string? Unit = null);

    /// <summary>Validates a backoff expression; the runtime parses it again, so only the verdict travels.</summary>
    public static bool TryParseBackoff(string expression, out Error error)
    {
        error = default;
        var parts = expression.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = new Error(ErrorKind.Invalid);
            return false;
        }

        var range = parts[0];
        var dots = range.IndexOf("..", StringComparison.Ordinal);
        var ranged = dots >= 0;
        if (
            !TryParseDuration(ranged ? range.Substring(0, dots) : range, out var initial, out error)
            || !TryParseDuration(ranged ? range.Substring(dots + 2) : range, out var max, out error)
            || max < initial
        )
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            var valid =
                part == "exact"
                || (
                    part.StartsWith("x", StringComparison.Ordinal)
                        ? TryNumber(part.Substring(1), out var multiplier) && multiplier >= 1.0 && multiplier <= 99999.9999
                        : IsJitter(part)
                );
            if (!valid)
            {
                error = new Error(ErrorKind.Invalid);
                return false;
            }
        }

        if (!TryToWholeSeconds(initial, out _) || !TryToWholeSeconds(max, out _))
        {
            error = new Error(ErrorKind.Invalid);
            return false;
        }

        return true;
    }

    public static bool TryParseDuration(string text, out TimeSpan span, out Error error)
    {
        var s = text.Trim();
        span = default;
        error = default;
        if (s.Length == 0)
        {
            error = new Error(ErrorKind.Invalid);
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
                    error = new Error(ErrorKind.Invalid);
                    return false;
                }
                return true;
            }
            catch (FormatException)
            {
                error = new Error(ErrorKind.Invalid);
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
            error = new Error(ErrorKind.Invalid);
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
                    error = new Error(IsCalendarOrUpperUnit(unit) ? ErrorKind.InvalidUnit : ErrorKind.Invalid, unit);
                    return false;
            }
        }
        catch (OverflowException)
        {
            // A huge numeral (e.g. "99999999999999999999d") must yield an ACTA0105 diagnostic, not crash
            // the source generator.
            span = default;
            error = new Error(ErrorKind.Invalid);
            return false;
        }
    }

    private static bool IsJitter(string text)
    {
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

        return number.EndsWith("%", StringComparison.Ordinal)
            && TryNumber(number.Substring(0, number.Length - 1), out var percent)
            && percent >= 0.0
            && percent <= 100.0;
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);

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
