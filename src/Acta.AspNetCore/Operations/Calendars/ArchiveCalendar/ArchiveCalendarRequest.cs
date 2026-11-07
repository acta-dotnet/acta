namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /calendars/{jobNamespace}/{calendarName}/archive</c>: the <c>ExpectedCurrentVersion</c> the operator last read, required.</summary>
/// <param name="ExpectedCurrentVersion">The number of the published version as the caller last read it; when another is in force the call
/// answers version-conflict and changes nothing.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record ArchiveCalendarRequest(int? ExpectedCurrentVersion, string? ReasonMessage = null) : IControlRequest;
