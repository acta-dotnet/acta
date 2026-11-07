namespace Acta.AspNetCore;

/// <summary>
/// Body of the calendar publication: the published version the operator last read, the draft to publish, what to do about overdue
/// occurrences, and an optional reason.
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
