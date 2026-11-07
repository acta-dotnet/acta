namespace Acta.AspNetCore;

/// <summary>Body of <c>PUT /namespaces/{jobNamespace}</c>. A null field clears it; <c>ExpectedVersion</c> drives the CAS.</summary>
/// <param name="OwnerTeam">The team that owns the namespace; omitted or null stores none.</param>
/// <param name="Description">The namespace's description; omitted or null stores none.</param>
/// <param name="ExpectedVersion">The version as the caller last read it; when the row has another version the call answers
/// version-conflict and changes nothing.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record UpdateNamespaceRequest(
    string? OwnerTeam = null,
    string? Description = null,
    int? ExpectedVersion = null,
    string? ReasonMessage = null
) : IControlRequest;
