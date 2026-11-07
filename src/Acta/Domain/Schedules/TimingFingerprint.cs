using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Acta;

/// <summary>
/// The identity of a schedule's timing: a hash over the declaration that decides its occurrences, the expression, its
/// kind, the zone id as declared, an interval's anchor, the availability calendar's name, and each bound that is set.
/// The zone enters by id alone, never by the rules the host reads for it, so every host hashes one declaration alike. A
/// calendar enters by name only, never by where it resolved, so a calendar whose content or source changes leaves the
/// fingerprint alone and the admitter's cursor heals the difference. A matching fingerprint keeps the cursor at
/// registration; a changed one is a timing change.
/// </summary>
internal static class TimingFingerprint
{
    /// <summary>Bumped whenever the evaluator's interpretation of an unchanged declaration changes.</summary>
    private const string EvaluatorVersion = "1";

    public static string Compute(ScheduleTiming timing)
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder(EvaluatorVersion)
            .Append('|')
            .Append(((byte)timing.ExpressionKind).ToString(c))
            .Append('|')
            .Append(timing.Expression)
            .Append('|')
            .Append(timing.IntervalAnchorAtUtc?.Ticks.ToString(c))
            .Append('|')
            .Append(timing.AvailabilityCalendarName)
            .Append('|');
        // A cron, a calendar, and an availability window resolve in the zone; a bare interval is absolute. The id is
        // normalized as registration stores it, a blank one meaning UTC.
        if (timing.ExpressionKind != ScheduleExpressionKindCode.Interval || timing.AvailabilityCalendarName is not null)
        {
            text.Append(string.IsNullOrWhiteSpace(timing.TimeZoneId) ? "UTC" : timing.TimeZoneId);
        }

        // A bound enters only when set: an empty field for an unset one would change every unbounded schedule's
        // fingerprint and retime it on the next deploy.
        if (timing.StartAtUtc is { } start)
        {
            text.Append("|S").Append(start.Ticks.ToString(c));
        }

        if (timing.EndBeforeUtc is { } end)
        {
            text.Append("|E").Append(end.Ticks.ToString(c));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
