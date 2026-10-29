using System.Text.Json;

namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /calendars/{jobNamespace}/{calendarName}/drafts</c>: the family's role, the definition as a JSON
/// object, and the <c>ExpectedLatestVersion</c> the operator last read, 0 for a new family.
/// </summary>
internal sealed record SaveCalendarDraftRequest(
    CalendarRoleCode Role,
    JsonElement Definition,
    int ExpectedLatestVersion,
    string? ReasonMessage = null
) : IControlRequest;
