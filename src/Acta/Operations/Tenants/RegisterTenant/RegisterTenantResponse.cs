using System.Text.Json.Serialization;

namespace Acta;

/// <summary>The tenant a registration created or found, by the name every other call and route addresses it with.</summary>
/// <param name="TenantId">The tenant's internal id; the JSON omits it and carries tenantName.</param>
/// <param name="TenantName">The tenant's name.</param>
public sealed record RegisterTenantResponse([property: JsonIgnore] int TenantId, string TenantName);
