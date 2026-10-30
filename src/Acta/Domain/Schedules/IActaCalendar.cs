namespace Acta;

/// <summary>
/// An occurrence source a schedule binds by name instead of a cron or interval expression: a fixed,
/// ordered set of instants that depends on nothing but the provider's own configuration and the context it is given.
/// Evaluation calls it with no lock held, so it must be quick and deterministic: no clock, no I/O, no ambient zone.
/// </summary>
public interface IActaCalendar
{
    /// <summary>
    /// The first occurrence strictly after <see cref="CalendarContext.AfterUtc"/>, a UTC instant at whole milliseconds,
    /// or null when no occurrence remains. An instant that is not UTC, does not advance, or is finer than a millisecond,
    /// and any exception, is an evaluation error the schedule retries, never exhaustion.
    /// </summary>
    DateTime? GetNextOccurrence(CalendarContext context);
}
