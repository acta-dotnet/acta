namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// Body of a job-control POST. Only the reason travels over HTTP; the framework stamps the actor
/// and reason code itself, so callers cannot forge the audit trail.
/// </summary>
internal sealed record JobControlRequest(string? ReasonMessage = null);

/// <summary>
/// Body of the job-control POSTs that accept a compare-and-set token (pause, resume, restart, cancel).
/// A null <c>ExpectedVersion</c> applies the verb unconditionally; a non-null one that misses the row's
/// current version answers 409 with action versionConflict and writes nothing. Purge and the admin
/// suspend/resume verbs take no token and keep the reason-only <see cref="JobControlRequest"/>.
/// </summary>
internal sealed record JobVersionedControlRequest(string? ReasonMessage = null, int? ExpectedVersion = null);

/// <summary>
/// Body of a job-reschedule POST. <c>NextRunAtUtc</c> is mandatory (missing or default is a 400); the
/// framework stamps the actor and reason code itself. <c>ExpectedVersion</c> is the optional CAS guard.
/// </summary>
internal sealed record JobRescheduleRequest(DateTime NextRunAtUtc = default, string? ReasonMessage = null, int? ExpectedVersion = null);

/// <summary>
/// Body of a job-reprioritize POST. <c>Priority</c> is mandatory; an unrecognized wire name fails
/// deserialization (400). The framework stamps the actor and reason code itself. <c>ExpectedVersion</c>
/// is the optional CAS guard.
/// </summary>
internal sealed record JobReprioritizeRequest(JobPriorityCode Priority, string? ReasonMessage = null, int? ExpectedVersion = null);

/// <summary>
/// Body of a job-input-amend POST. Exactly one of <c>Input</c> (raw JSON, stored as json), <c>Text</c>
/// (raw text, stored as text), or <c>Base64</c> (stored as the job's current binary format) must be
/// present. The chosen field must match the job's stored input format, except that <c>Input</c> is
/// accepted as a json fallback for any non-none format. <c>ReasonMessage</c> carries the operator's why;
/// the framework stamps the actor and reason code itself.
/// </summary>
internal sealed record JobInputRequest(
    // Nullable rather than a bare JsonElement: the absent case has to be a value the schema generator
    // can write as this parameter's default, and an uninitialized JsonElement has nothing to write.
    System.Text.Json.JsonElement? Input = null,
    string? Text = null,
    string? Base64 = null,
    string? ReasonMessage = null
);
