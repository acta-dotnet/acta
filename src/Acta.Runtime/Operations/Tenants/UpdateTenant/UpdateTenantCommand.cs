namespace Acta.Runtime;

/// <summary>One tenant edit as the facade received it; null clears a field.</summary>
internal sealed record UpdateTenantCommand(
    string TenantKey,
    int ExpectedVersion,
    string? DisplayName,
    string? Description,
    string? ReasonMessage,
    string? ActorKey
);
