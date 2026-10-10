namespace Acta;

/// <summary>One tenant registration as the facade received it.</summary>
internal sealed record RegisterTenantCommand(string TenantName, string? DisplayName, string? Description);
