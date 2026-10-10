namespace Acta;

/// <summary>One tenant resumption as the facade received it.</summary>
internal sealed record ResumeTenantCommand(string TenantName, string? ReasonMessage, string? Actor);
