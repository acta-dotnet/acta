namespace Acta.AspNetCore;

/// <summary>Body of the calendar archive: optionally the published version the operator last read, and an optional reason.</summary>
/// <param name="ExpectedCurrentVersion">The number of the published version as the caller last read it; when another is in force the call
/// answers rejected and changes nothing. Leave it out to archive whatever is in force.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record ArchiveCalendarRequest(long? ExpectedCurrentVersion, string? ReasonMessage = null) : IControlRequest;
