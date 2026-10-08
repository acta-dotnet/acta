namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /alerts/{alertRef}/acknowledge</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record AcknowledgeAlertRequest(string? ReasonMessage = null) : IControlRequest;
