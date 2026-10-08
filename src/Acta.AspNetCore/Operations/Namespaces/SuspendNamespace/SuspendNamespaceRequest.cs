namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /namespaces/{jobNamespace}/suspend</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record SuspendNamespaceRequest(string? ReasonMessage = null) : IControlRequest;
