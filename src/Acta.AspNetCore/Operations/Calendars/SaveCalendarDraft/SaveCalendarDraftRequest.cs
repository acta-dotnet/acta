using System.Text.Json;

namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /calendars/{jobNamespace}/{calendarName}/drafts</c>: the family's role, the definition as a JSON
/// object, and the <c>ExpectedLatestVersion</c> the operator last read, 0 for a new family, required.
/// </summary>
/// <param name="Role">The role of the family the draft belongs to.</param>
/// <param name="Definition">The calendar's definition, as a JSON object.</param>
/// <param name="ExpectedLatestVersion">The number of the family's newest version as the caller last read it; when another is newest the
/// call answers version-conflict and changes nothing.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record SaveCalendarDraftRequest(
    CalendarRoleCode Role,
    JsonElement Definition,
    int? ExpectedLatestVersion,
    string? ReasonMessage = null
) : IControlRequest;
