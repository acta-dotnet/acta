namespace Acta.AspNetCore.Features.Schedules;

/// <summary>
/// Optional body of a schedule pause POST (the schedule is addressed by the route's natural key).
/// <c>PausedUntilUtc</c> null is an indefinite pause, a timestamp is a timed pause. The framework
/// stamps the actor itself, so the body carries only the pause window and the operator note.
/// Resume and trigger carry only a <c>reasonMessage</c> and share the generic control-request shape.
/// </summary>
internal sealed record SchedulePauseRequest(DateTime? PausedUntilUtc = null, string? ReasonMessage = null);

/// <summary>
/// Body of a schedule overrides POST (the schedule is addressed by the route's natural key).
/// <c>ExpectedVersion</c> is the CAS token (the schedule's current <c>version</c>). Full-set
/// semantics: a null <c>Expression</c>/<c>TimeZoneId</c> clears that override.
/// </summary>
internal sealed record SetScheduleOverridesRequest(
    int? ExpectedVersion = null,
    string? Expression = null,
    string? TimeZoneId = null,
    string? ReasonMessage = null
);
