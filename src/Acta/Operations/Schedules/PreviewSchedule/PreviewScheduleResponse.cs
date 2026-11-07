namespace Acta;

/// <summary>
/// A read-only forecast of a schedule's upcoming fire instants, computed live (no persisted state is
/// read or written). <see cref="Expression"/> and <see cref="TimeZoneId"/> are the effective values
/// (operator override when present, else the original); <see cref="NextOccurrencesUtc"/> is strictly
/// increasing and may be shorter than requested when the expression is exhausted.
/// </summary>
/// <param name="Expression">The cron expression, the interval such as 5m or PT5M, or the calendar's name.</param>
/// <param name="TimeZoneId">The IANA time zone the schedule's expression is evaluated in.</param>
/// <param name="NextOccurrencesUtc">The schedule's next occurrences, in UTC, soonest first.</param>
public sealed record PreviewScheduleResponse(string Expression, string TimeZoneId, IReadOnlyList<DateTime> NextOccurrencesUtc);
