namespace Acta;

/// <summary>One namespace edit as the facade received it; null clears a field.</summary>
internal sealed record UpdateNamespaceCommand(
    string NamespaceName,
    long ExpectedVersion,
    string? OwnerTeam,
    string? Description,
    string? ReasonMessage,
    string? Actor
);
