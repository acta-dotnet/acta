using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using static Acta.Generators.ActaManifestGenerator;
using static Acta.Generators.JobPolicyValues;
using static Acta.Generators.JobSignatureReader;

namespace Acta.Generators;

/// <summary>Reads the <c>[JobSchedule]</c> attributes on a handler and checks each expression.</summary>
internal static class JobScheduleReader
{
    internal static ImmutableArray<DiscoveredSchedule> ReadSchedules(IMethodSymbol method, string jobName)
    {
        var builder = ImmutableArray.CreateBuilder<DiscoveredSchedule>();
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass is null || TypeFullName(attr.AttributeClass) != JobScheduleAttributeMetadataName)
            {
                continue;
            }
            if (attr.ConstructorArguments.Length < 2)
            {
                continue;
            }

            // Malformed names/expressions/environments are kept verbatim; Emit diagnoses them
            // (ACTA0121/ACTA0122) instead of silently dropping the schedule.
            var scheduleName = attr.ConstructorArguments[0].Value as string ?? "";
            var expression = attr.ConstructorArguments[1].Value as string ?? "";

            // Match JobScheduleAttribute's runtime default and make the default explicit in the
            // generated descriptor so persistence never receives an empty time-zone sentinel.
            string? timeZone = "UTC";
            string? description = null;
            var misfireName = "Skip";
            var environments = ImmutableArray<string>.Empty;

            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "TimeZoneId" when named.Value.Value is string tz && !string.IsNullOrWhiteSpace(tz):
                        timeZone = tz;
                        break;
                    case "Description" when named.Value.Value is string d && !string.IsNullOrWhiteSpace(d):
                        description = d;
                        break;
                    // MisfireStrategyCode is byte-backed; read it through TryGetEnumByte rather than an
                    // `is int` pattern that never matches a boxed byte and silently drops the value.
                    case "MisfireStrategy" when TryGetEnumByte(named.Value, out var mf):
                        misfireName = mf == 20 ? "Skip" : "CatchUpOnce";
                        break;
                    case "Environments" when !named.Value.IsNull:
                        environments = [.. named.Value.Values.Select(v => v.Value as string ?? "")];
                        break;
                }
            }

            var kindName = InferExpressionKindName(expression);
            builder.Add(
                new DiscoveredSchedule(jobName, scheduleName, expression, timeZone, misfireName, kindName, description, environments)
            );
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// A single-token expression starting with a digit (human form, e.g. `5m`) or `P`/`p` (ISO 8601, e.g.
    /// `PT5M`/`P1D`) is an interval; a space-separated or macro expression (e.g. `0 5 * * *`, `@daily`) is cron.
    /// </summary>
    internal static string InferExpressionKindName(string expression)
    {
        var e = expression.Trim();
        return e.Length > 0 && e.IndexOf(' ') < 0 && (e[0] is 'P' or 'p' || char.IsDigit(e[0])) ? "Interval" : "Cron";
    }

    /// <summary>
    /// ACTA0121 / ACTA0122: declaration and expression validity for every [JobSchedule].
    /// </summary>
    internal static bool ValidateSchedules(SourceProductionContext spc, DiscoveredJob job, bool isFrameworkAssembly)
    {
        if (job.Schedules.IsDefaultOrEmpty)
        {
            return true;
        }

        var ok = true;
        if (job.Lane is not null)
        {
            ReportDiagnostic(spc, ManifestDiagnostics.LanedSchedule(job));
            ok = false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schedule in job.Schedules)
        {
            if (!KebabName.IsValid(schedule.ScheduleName, maxLength: 128, allowSystemPrefix: isFrameworkAssembly))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.InvalidScheduleName(job, schedule.ScheduleName));
                ok = false;
            }
            else if (!seen.Add(schedule.ScheduleName) && duplicated.Add(schedule.ScheduleName))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.DuplicateScheduleName(job, schedule.ScheduleName));
                ok = false;
            }

            if (string.IsNullOrWhiteSpace(schedule.Expression))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.BlankScheduleExpression(job, schedule.ScheduleName));
                ok = false;
            }
            else if (!IsValidScheduleExpression(schedule.Expression, schedule.ExpressionKindName))
            {
                ReportDiagnostic(spc, ManifestDiagnostics.InvalidScheduleExpression(job, schedule.ScheduleName, schedule.Expression));
                ok = false;
            }

            foreach (var env in schedule.Environments)
            {
                if (string.IsNullOrWhiteSpace(env))
                {
                    ReportDiagnostic(spc, ManifestDiagnostics.BlankScheduleEnvironment(job, schedule.ScheduleName));
                    ok = false;
                }
            }
        }
        return ok;
    }

    /// <summary>
    /// Mirrors NextOccurrenceCalculator.ParseInterval: an interval is the human form (number + ms/s/m/h/d)
    /// or an ISO 8601 duration via XmlConvert, of at least one second, as often as cron can fire, which keeps a slot's
    /// 32-bit counters inside their range for decades; cron uses the conservative Cronos-dialect validator.
    /// </summary>
    internal static bool IsValidScheduleExpression(string expression, string kindName)
    {
        return kindName == "Interval"
            ? TryParseInterval(expression.Trim(), out var span) && span >= TimeSpan.FromSeconds(1)
            : CronExpressionValidator.IsValid(expression);
    }

    internal static bool TryParseInterval(string e, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (e.Length == 0)
        {
            return false;
        }
        if (e[0] is 'P' or 'p')
        {
            try
            {
                span = System.Xml.XmlConvert.ToTimeSpan(e);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        var i = 0;
        while (i < e.Length && (char.IsDigit(e[i]) || e[i] == '.'))
        {
            i++;
        }
        if (i == 0 || i == e.Length)
        {
            return false;
        }
        if (
            !double.TryParse(
                e.Substring(0, i),
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out var n
            )
        )
        {
            return false;
        }
        try
        {
            switch (e.Substring(i))
            {
                case "ms":
                    span = TimeSpan.FromMilliseconds(n);
                    return true;
                case "s":
                    span = TimeSpan.FromSeconds(n);
                    return true;
                case "m":
                    span = TimeSpan.FromMinutes(n);
                    return true;
                case "h":
                    span = TimeSpan.FromHours(n);
                    return true;
                case "d":
                    span = TimeSpan.FromDays(n);
                    return true;
                default:
                    return false;
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

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

        if (body.Length == 0)
        {
            return false;
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

    /// <summary>
    /// The dash separating a range, searched from position 1 so a leading or lone "-" never splits to an empty part.
    /// </summary>
    private static int IndexOfRangeDash(string body) => body.Length > 1 ? body.IndexOf('-', 1) : -1;

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
