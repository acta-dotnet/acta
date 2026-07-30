namespace Acta.Modules.Execution.Namespaces;

/// <summary>
/// Flat namespace admin-list row projected from storage. The ctor order is the positional
/// <c>[DbProjection]</c> contract: it must match the SELECT column order in every provider's
/// <c>ListNamespaceItems.sql</c> at every position.
/// </summary>
internal sealed record NamespaceListRow(
    short Id,
    string Name,
    JobNamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
)
{
    public NamespaceListItem ToItem() => new(Id, Name, Status, OwnerTeam, Description, Version);
}
