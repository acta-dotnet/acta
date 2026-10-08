namespace Acta.AspNetCore;

/// <summary>
/// Body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/overrides</c>. <c>ExpectedVersion</c> is the CAS
/// token, the schedule's current <c>version</c>. Full-set semantics: a null <c>Expression</c> or <c>TimeZoneId</c>
/// clears that override.
/// </summary>
internal sealed record SetScheduleOverridesRequest(
    int? ExpectedVersion = null,
    string? Expression = null,
    string? TimeZoneId = null,
    string? ReasonMessage = null
) : IControlRequest;
