namespace Acta.AspNetCore;

/// <summary>
/// Body of the calendar publication: the draft to publish, optionally the published version the operator last read, what to do about
/// overdue occurrences, and an optional reason.
/// </summary>
/// <param name="ExpectedCurrentVersion">The number of the published version as the caller last read it; 0 when none is published.
/// When another is in force, the call answers version-conflict and changes nothing; leave it out to publish whatever is in force.</param>
/// <param name="CandidateVersion">The number of the draft version to publish; the version already in force answers already-in-state
/// and retimes nothing.</param>
/// <param name="OverdueDecision">What to do when a schedule that names the family holds an overdue occurrence; refuse, the default,
/// rejects the publication.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record PublishCalendarRequest(
    int? ExpectedCurrentVersion,
    int? CandidateVersion,
    OverdueDecisionCode OverdueDecision = OverdueDecisionCode.Refuse,
    string? ReasonMessage = null
) : IControlRequest;
