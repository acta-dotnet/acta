namespace Acta;

/// <summary>
/// One namespace row in an <see cref="INamespaces.ListItemsAsync"/> page, projected from the
/// <c>namespaces</c> table. This is the admin-list read (name, status, metadata, version); the plain
/// name list used by the dashboard scope selector stays on <see cref="INamespaces.ListAsync"/>.
/// </summary>
/// <param name="Id">DB-assigned namespace id, referenced by <c>job.namespace_id</c>. The seeded sys namespace is id 1.</param>
/// <param name="Name">Operator-readable kebab-case namespace name. Unique.</param>
/// <param name="Status">Namespace lifecycle status (Active resolves at enqueue; Suspended rejects it).</param>
/// <param name="OwnerTeam">Owning-team identifier surfaced on dashboards, or null.</param>
/// <param name="Description">Operator-readable description, or null.</param>
/// <param name="Version">Optimistic-concurrency row version; pass as the expected version to a CAS control verb.</param>
public sealed record NamespaceListItem(
    short Id,
    string Name,
    JobNamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
