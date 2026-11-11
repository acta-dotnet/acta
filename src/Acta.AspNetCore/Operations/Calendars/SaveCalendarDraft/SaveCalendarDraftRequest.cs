using System.Text.Json;

namespace Acta.AspNetCore;

/// <summary>
/// Body of the calendar draft: the family's role, the definition, optionally the newest version the operator last read, and an
/// optional reason.
/// </summary>
/// <param name="Role">The role of the family the draft belongs to.</param>
/// <param name="Definition">The calendar's definition, as a JSON object.</param>
/// <param name="ExpectedLatestVersion">The number of the family's newest version as the caller last read it; 0 for a family that has no
/// version. When another is newest, the call answers version-conflict and changes nothing; leave it out to save whatever is newest.
/// A definition equal to the newest version's answers already-in-state either way.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record SaveCalendarDraftRequest(
    CalendarRoleCode Role,
    JsonElement Definition,
    long? ExpectedLatestVersion,
    string? ReasonMessage = null
) : IControlRequest;
