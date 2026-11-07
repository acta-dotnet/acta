namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /alerts/{alertRef}/resolve</c>: the operator's reason, optional like the body itself.</summary>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record ResolveAlertRequest(string? ReasonMessage = null) : IControlRequest;
