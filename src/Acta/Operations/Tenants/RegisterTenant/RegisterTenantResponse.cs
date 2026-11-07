using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The tenant a registration created or found: its stable numeric id, and its canonical key, which is how every other
/// call and route addresses it.
/// </summary>
/// <param name="TenantId">The tenant's internal id; the JSON omits it and carries tenantName.</param>
/// <param name="TenantName">The tenant's name.</param>
public sealed record RegisterTenantResponse([property: JsonIgnore] int TenantId, string TenantName);
