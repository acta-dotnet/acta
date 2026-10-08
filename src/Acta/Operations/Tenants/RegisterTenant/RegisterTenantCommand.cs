namespace Acta;

/// <summary>One tenant registration as the facade received it.</summary>
internal sealed record RegisterTenantCommand(string TenantKey, string? DisplayName, string? Description);
