namespace Acta.AspNetCore.Features.Jobs;

/// <summary>
/// Body of a job-control POST. Only the reason travels over HTTP; the framework stamps the actor
/// and reason code itself, so callers cannot forge the audit trail.
/// </summary>
internal sealed record JobControlRequest(string? ReasonMessage = null);

/// <summary>
/// Body of a job-reschedule POST. <c>NextRunAtUtc</c> is mandatory (missing or default is a 400); the
/// framework stamps the actor and reason code itself.
/// </summary>
internal sealed record JobRescheduleRequest(DateTime NextRunAtUtc = default, string? ReasonMessage = null);

/// <summary>
/// Body of a job-reprioritize POST. <c>Priority</c> is mandatory; an unrecognized wire name fails
/// deserialization (400). The framework stamps the actor and reason code itself.
/// </summary>
internal sealed record JobReprioritizeRequest(JobPriorityCode Priority, string? ReasonMessage = null);
