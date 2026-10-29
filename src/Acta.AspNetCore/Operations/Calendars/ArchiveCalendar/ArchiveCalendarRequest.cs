namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /calendars/{jobNamespace}/{calendarName}/archive</c>: the <c>ExpectedCurrentVersion</c> the operator last read.</summary>
internal sealed record ArchiveCalendarRequest(int ExpectedCurrentVersion, string? ReasonMessage = null) : IControlRequest;
