using System.Text.Json.Serialization;

namespace Acta;

/// <summary>One tenant in a page.</summary>
/// <param name="TenantId">The tenant's internal id; the JSON omits it and carries tenantName.</param>
/// <param name="TenantName">The tenant's name.</param>
/// <param name="DisplayName">The tenant's display name.</param>
/// <param name="Description">The tenant's description, when one is recorded.</param>
/// <param name="Status">The tenant's status.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="Version">The tenant's version, which every change to it increments; send it as the expected version of a control.</param>
public sealed record ListTenantsItem(
    [property: JsonIgnore] int TenantId,
    string TenantName,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
);
