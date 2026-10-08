namespace Acta.AspNetCore;

/// <summary>Body of <c>PATCH /namespaces/{jobNamespace}</c>. A null field clears it; <c>ExpectedVersion</c> drives the CAS.</summary>
internal sealed record UpdateNamespaceRequest(
    string? OwnerTeam = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
) : IControlRequest;
