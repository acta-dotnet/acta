namespace Acta;

/// <summary>One namespace edit as the facade received it; null clears a field.</summary>
internal sealed record UpdateNamespaceCommand(
    string Name,
    int ExpectedVersion,
    string? OwnerTeam,
    string? Description,
    string? ReasonMessage,
    string? Actor
);
