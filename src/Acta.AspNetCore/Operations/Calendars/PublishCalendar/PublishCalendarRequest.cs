namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /calendars/{jobNamespace}/{calendarName}/publish</c>: the <c>ExpectedCurrentVersion</c> the operator
/// last read (0 for none) and the draft to publish, both required, and what to do with the overdue occurrences the
/// current version leaves.
/// </summary>
/// <param name="ExpectedCurrentVersion">The number of the published version as the caller last read it; when another is in force the call
/// answers version-conflict and changes nothing.</param>
/// <param name="CandidateVersion">The number of the draft version to publish.</param>
/// <param name="OverdueDecision">What to do when a schedule that names the family holds an overdue occurrence; refuse, the default,
/// rejects the publication.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record PublishCalendarRequest(
    int? ExpectedCurrentVersion,
    int? CandidateVersion,
    OverdueDecisionCode OverdueDecision = OverdueDecisionCode.Refuse,
    string? ReasonMessage = null
) : IControlRequest;
