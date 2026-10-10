using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The tenant a registration created or found: its stable numeric id, and its canonical key, which is how every other
/// call and route addresses it.
/// </summary>
public sealed record RegisterTenantResponse([property: JsonIgnore] int TenantId, string TenantName);
