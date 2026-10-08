namespace Acta.AspNetCore;

/// <summary>Body of <c>POST /namespaces/{jobNamespace}/resume</c>: the operator's reason, optional like the body itself.</summary>
internal sealed record ResumeNamespaceRequest(string? ReasonMessage = null) : IControlRequest;
