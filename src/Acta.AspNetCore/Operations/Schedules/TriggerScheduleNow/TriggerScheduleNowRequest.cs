namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/trigger</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record TriggerScheduleNowRequest(string? ReasonMessage = null) : IControlRequest;
