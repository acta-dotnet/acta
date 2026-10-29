namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /calendars/{jobNamespace}/{calendarName}/publish</c>: the <c>ExpectedCurrentVersion</c> the operator
/// last read (0 for none), the draft to publish, and what to do with the overdue occurrences the current version leaves.
/// </summary>
internal sealed record PublishCalendarRequest(
    int ExpectedCurrentVersion,
    int CandidateVersion,
    OverdueDecisionCode OverdueDecision = OverdueDecisionCode.Refuse,
    string? ReasonMessage = null
) : IControlRequest;
