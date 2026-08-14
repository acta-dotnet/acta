namespace Acta.AspNetCore.Features.Schedules;

/// <summary>
/// Body of a schedule pause POST. The schedule is addressed by natural key (namespace, job name,
/// schedule name); <c>PausedUntilUtc</c> null is an indefinite pause, a timestamp is a timed pause.
/// The framework stamps the actor itself, so the body carries only the operator note.
/// </summary>
internal sealed record SchedulePauseRequest(
    string? JobNamespace = null,
    string? JobName = null,
    string? ScheduleName = null,
    DateTime? PausedUntilUtc = null,
    string? ReasonMessage = null
);

/// <summary>
/// Body of a schedule resume POST. The schedule is addressed by natural key (namespace, job name,
/// schedule name); the body carries only the operator note.
/// </summary>
internal sealed record ScheduleResumeRequest(
    string? JobNamespace = null,
    string? JobName = null,
    string? ScheduleName = null,
    string? ReasonMessage = null
);

/// <summary>
/// Body of a schedule trigger-now POST. The schedule is addressed by natural key (namespace, job name,
/// schedule name); the body carries only the operator note.
/// </summary>
internal sealed record ScheduleTriggerRequest(
    string? JobNamespace = null,
    string? JobName = null,
    string? ScheduleName = null,
    string? ReasonMessage = null
);

/// <summary>
/// Body of a schedule overrides POST. The schedule is addressed by natural key (namespace, job name,
/// schedule name); <c>ExpectedVersion</c> is the CAS token (the schedule's current <c>version</c>). Full-set
/// semantics: a null <c>Expression</c>/<c>TimeZoneId</c> clears that override.
/// </summary>
internal sealed record SetScheduleOverridesRequest(
    string? JobNamespace = null,
    string? JobName = null,
    string? ScheduleName = null,
    int? ExpectedVersion = null,
    string? Expression = null,
    string? TimeZoneId = null,
    string? ReasonMessage = null
);
