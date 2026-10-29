namespace Acta;

/// <summary>
/// <see cref="ICalendars.PublishAsync"/>: make <see cref="CandidateVersion"/> the family's current version, given the
/// current one the caller saw (0 for none), deciding the overdue occurrences the old version left undecided.
/// </summary>
internal sealed record PublishCalendarCommand(
    CalendarLookup Calendar,
    int ExpectedCurrentVersion,
    int CandidateVersion,
    OverdueDecisionCode Overdue,
    string? ReasonMessage,
    string? Actor
);
