namespace Acta.Runtime.Modules.Execution.Tenants;

/// <summary>
/// Flat tenant list row projected from storage.
/// </summary>
internal sealed record TenantListRow(
    int TenantId,
    string TenantKey,
    string? DisplayName,
    string? Description,
    TenantStatusCode Status,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    int Version
)
{
    public TenantListItem ToItem() => new(TenantId, TenantKey, DisplayName, Description, Status, CreatedAtUtc, ModifiedAtUtc, Version);
}
