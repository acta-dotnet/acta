namespace Acta.Runtime;

/// <summary>One tenant suspension as the facade received it.</summary>
internal sealed record SuspendTenantCommand(string TenantKey, string? ReasonMessage, string? ActorKey);
