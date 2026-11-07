namespace Acta;

/// <summary>
/// A forecast of a schedule's next fire instants, walked from the database clock over the stored schedule's expression, time zone,
/// and calendars; no cursor is read or moved, and a paused schedule previews as if it were active. nextOccurrencesUtc is strictly
/// increasing and is shorter than asked when the expression runs out.
/// </summary>
/// <param name="Expression">The cron expression, the interval such as 5m or PT5M, or the calendar's name.</param>
/// <param name="TimeZoneId">The IANA time zone the schedule's expression is evaluated in.</param>
/// <param name="NextOccurrencesUtc">The schedule's next occurrences, in UTC, soonest first.</param>
public sealed record PreviewScheduleResponse(string Expression, string TimeZoneId, IReadOnlyList<DateTime> NextOccurrencesUtc);
