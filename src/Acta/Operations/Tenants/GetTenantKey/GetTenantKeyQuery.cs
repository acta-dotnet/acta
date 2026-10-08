namespace Acta;

/// <summary>The key of the tenant with this id, for the engine, which holds only the id.</summary>
internal sealed record GetTenantKeyQuery(int TenantId);
