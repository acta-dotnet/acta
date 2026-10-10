namespace Acta;

/// <summary>One tenant suspension as the facade received it.</summary>
internal sealed record SuspendTenantCommand(string TenantName, string? ReasonMessage, string? Actor);
