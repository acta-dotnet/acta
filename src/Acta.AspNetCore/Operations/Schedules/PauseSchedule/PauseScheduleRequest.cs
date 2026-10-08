namespace Acta.AspNetCore;

/// <summary>
/// Optional body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause</c>. A null
/// <c>PausedUntilUtc</c> is an indefinite pause, a timestamp a timed one.
/// </summary>
internal sealed record PauseScheduleRequest(DateTime? PausedUntilUtc = null, string? ReasonMessage = null) : IControlRequest;
