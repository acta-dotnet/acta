using System;
using System.Linq;
using static Acta.Generators.ActaManifestGenerator;

namespace Acta.Generators;

/// <summary>
/// ACTA0108: a job whose schedule can fire more than once a minute and that does not state what Acta keeps of it.
/// Every firing writes events, and a per-occurrence firing also makes a job.
/// </summary>
internal static class ScheduleRate
{
    private const long MillisecondsPerYear = 365L * 24 * 3600 * 1000;

    /// <summary>
    /// The warning for the job's fastest schedule under a minute, or null. A permanent job is silenced by stating
    /// <c>EventRecording</c>, a per-occurrence job by stating both <c>EventRecording</c> and <c>JobRetention</c>.
    /// </summary>
    internal static DiagnosticRecord? Warning(DiscoveredJob job)
    {
        if (job.StatesEventRecording && (job.ScheduledDeliveryName == "Permanent" || job.StatesJobRetention))
        {
            return null;
        }

        // The shortest interval is the fastest schedule, ahead of any cron, since the build does not compute a cron's rate.
        DiscoveredSchedule? interval = null;
        DiscoveredSchedule? cron = null;
        var shortest = TimeSpan.FromMinutes(1);
        foreach (var schedule in job.Schedules)
        {
            if (
                schedule.ExpressionKindName == "Interval"
                && ScheduleReader.TryParseInterval(schedule.Expression.Trim(), out var span)
                && span < shortest
            )
            {
                interval = schedule;
                shortest = span;
            }
            else if (schedule.ExpressionKindName == "Cron" && cron is null && CanFireMoreThanOnceAMinute(schedule.Expression))
            {
                cron = schedule;
            }
        }

        return interval is not null
                ? ManifestDiagnostics.FrequentInterval(job, interval, MillisecondsPerYear / (long)shortest.TotalMilliseconds)
            : cron is not null ? ManifestDiagnostics.FrequentCron(job, cron)
            : null;
    }

    /// <summary>
    /// A cron with a seconds field can fire more than once a minute unless that field is one plain number, and so
    /// can <c>@every_second</c>.
    /// </summary>
    private static bool CanFireMoreThanOnceAMinute(string expression)
    {
        var expr = expression.Trim();
        if (expr.StartsWith("@", StringComparison.Ordinal))
        {
            return string.Equals(expr, "@every_second", StringComparison.OrdinalIgnoreCase);
        }
        var fields = expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 6 && !fields[0].All(char.IsDigit);
    }
}
