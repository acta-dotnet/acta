namespace Acta.Runtime;

/// <summary>One tenant resumption as the facade received it.</summary>
internal sealed record ResumeTenantCommand(string TenantKey, string? ReasonMessage, string? ActorKey);
