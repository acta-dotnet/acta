namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /schedules/{jobNamespace}/{jobName}/{scheduleName}/resume</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record ResumeScheduleRequest(string? ReasonMessage = null) : IControlRequest;
