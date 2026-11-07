namespace Acta;

/// <summary>
/// <see cref="ICalendars.PublishAsync"/>: make <see cref="CandidateVersion"/> the family's current version, given the
/// current one the caller saw (0 for none, null for no check), deciding the overdue occurrences the old version left undecided.
/// </summary>
internal sealed record PublishCalendarCommand(
    CalendarLookup Calendar,
    int CandidateVersion,
    int? ExpectedCurrentVersion,
    OverdueDecisionCode Overdue,
    string? ReasonMessage,
    string? Actor
);
