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
    /// or a positive ISO 8601 duration via XmlConvert; cron uses the conservative Cronos-dialect validator.
    /// </summary>
    internal static bool IsValidScheduleExpression(string expression, string kindName)
    {
        return kindName == "Interval"
            ? TryParseInterval(expression.Trim(), out var span) && span > TimeSpan.Zero
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
