namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /alerts/{alertRef}/resolve</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record ResolveAlertRequest(string? ReasonMessage = null) : IControlRequest;
