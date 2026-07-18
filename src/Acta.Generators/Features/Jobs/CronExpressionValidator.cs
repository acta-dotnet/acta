using System;
using System.Linq;

namespace Acta.Generators.Features.Jobs;

/// <summary>
/// Structural compile-time validation of cron expressions in the Cronos dialect (5 fields, or 6
/// with leading seconds). The runtime parses with the Cronos package, which a netstandard2.0
/// analyzer cannot reference; this validator accepts a small superset of Cronos so a valid
/// expression is never rejected, while field counts, token shapes, and numeric ranges still fail
/// at build time.
/// </summary>
internal static class CronExpressionValidator
{
    private static readonly string[] Macros =
    [
        "@yearly",
        "@annually",
        "@monthly",
        "@weekly",
        "@daily",
        "@midnight",
        "@hourly",
        "@every_minute",
        "@every_second",
    ];

    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private sealed record FieldSpec(int Min, int Max, string[]? Names, bool AllowQuestion, bool AllowL, bool AllowW, bool AllowHash);

    private static readonly FieldSpec[] Specs =
    [
        new(0, 59, null, AllowQuestion: false, AllowL: false, AllowW: false, AllowHash: false), // second
        new(0, 59, null, AllowQuestion: false, AllowL: false, AllowW: false, AllowHash: false), // minute
        new(0, 23, null, AllowQuestion: false, AllowL: false, AllowW: false, AllowHash: false), // hour
        new(1, 31, null, AllowQuestion: true, AllowL: true, AllowW: true, AllowHash: false), // day of month
        new(1, 12, MonthNames, AllowQuestion: false, AllowL: false, AllowW: false, AllowHash: false), // month
        new(0, 7, DayNames, AllowQuestion: true, AllowL: true, AllowW: false, AllowHash: true), // day of week
    ];

    public static bool IsValid(string expression)
    {
        var expr = expression.Trim();
        if (expr.StartsWith("@", StringComparison.Ordinal))
        {
            return Macros.Contains(expr.ToLowerInvariant());
        }

        var fields = expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5 && fields.Length != 6)
        {
            return false;
        }

        var offset = fields.Length == 6 ? 0 : 1;
        for (var i = 0; i < fields.Length; i++)
        {
            if (!FieldIsValid(fields[i], Specs[i + offset]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool FieldIsValid(string field, FieldSpec spec)
    {
        foreach (var token in field.Split(','))
        {
            if (!TokenIsValid(token, spec))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TokenIsValid(string token, FieldSpec spec)
    {
        if (token.Length == 0)
        {
            return false;
        }

        // Optional "/step" suffix on a range, "*", or a start value.
        var body = token;
        var slash = token.IndexOf('/');
        if (slash >= 0)
        {
            body = token.Substring(0, slash);
            var step = token.Substring(slash + 1);
            if (!TryParseInt(step, out var stepValue) || stepValue < 1)
            {
                return false;
            }
        }

        if (body == "*" || (body == "?" && spec.AllowQuestion))
        {
            return true;
        }

        // Day-of-month specials: L, L-offset, LW, nW.
        if (spec.AllowW)
        {
            if (body == "LW")
            {
                return slash < 0;
            }
            if (body.Length > 1 && body[body.Length - 1] == 'W')
            {
                return slash < 0 && TryParseValue(body.Substring(0, body.Length - 1), spec, out _);
            }
        }
        if (spec.AllowL)
        {
            if (body == "L")
            {
                return slash < 0;
            }
            if (!spec.AllowHash && body.StartsWith("L-", StringComparison.Ordinal))
            {
                return slash < 0 && TryParseInt(body.Substring(2), out var back) && back >= 1 && back <= 30;
            }
            // Day-of-week "nL" (last weekday n of the month).
            if (spec.AllowHash && body.Length > 1 && body[body.Length - 1] == 'L')
            {
                return slash < 0 && TryParseValue(body.Substring(0, body.Length - 1), spec, out _);
            }
        }

        // Day-of-week "n#m" (m-th weekday n of the month).
        if (spec.AllowHash)
        {
            var hash = body.IndexOf('#');
            if (hash >= 0)
            {
                return slash < 0
                    && TryParseValue(body.Substring(0, hash), spec, out _)
                    && TryParseInt(body.Substring(hash + 1), out var nth)
                    && nth >= 1
                    && nth <= 5;
            }
        }

        // value or value-value range.
        var dash = IndexOfRangeDash(body);
        if (dash < 0)
        {
            return TryParseValue(body, spec, out _);
        }
        return TryParseValue(body.Substring(0, dash), spec, out _) && TryParseValue(body.Substring(dash + 1), spec, out _);
    }

    // The dash separating a range, skipping position 0 so a lone "-" never splits to empty parts.
    private static int IndexOfRangeDash(string body) => body.IndexOf('-', 1 > body.Length - 1 ? body.Length - 1 : 1);

    private static bool TryParseValue(string text, FieldSpec spec, out int value)
    {
        if (TryParseInt(text, out value))
        {
            return value >= spec.Min && value <= spec.Max;
        }
        if (spec.Names is not null)
        {
            var index = Array.IndexOf(spec.Names, text.ToUpperInvariant());
            if (index >= 0)
            {
                // Names map onto the numeric domain: months are 1-based, weekdays 0-based.
                value = spec.Min == 1 ? index + 1 : index;
                return true;
            }
        }
        return false;
    }

    private static bool TryParseInt(string text, out int value)
    {
        value = 0;
        if (text.Length == 0)
        {
            return false;
        }
        foreach (var c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            value = value * 10 + (c - '0');
            if (value > 1000)
            {
                return false;
            }
        }
        return true;
    }
}
