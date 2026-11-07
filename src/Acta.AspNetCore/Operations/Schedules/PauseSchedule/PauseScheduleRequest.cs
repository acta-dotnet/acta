namespace Acta.AspNetCore;

/// <summary>
/// Optional body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/pause</c>. A null
/// <c>PausedUntilUtc</c> is an indefinite pause, a timestamp a timed one.
/// </summary>
/// <param name="PausedUntilUtc">When the pause should end, in UTC; omit it to pause until the schedule is resumed.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record PauseScheduleRequest(DateTime? PausedUntilUtc = null, string? ReasonMessage = null) : IControlRequest;
