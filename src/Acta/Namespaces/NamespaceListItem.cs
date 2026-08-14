namespace Acta;

/// <summary>
/// One namespace row in an <see cref="INamespaces.ListAsync"/> page, projected from the
/// <c>namespaces</c> table, and the one representation <c>GET /namespaces</c> answers with.
/// <see cref="INamespaces.ListNamesAsync"/> remains for callers that want only the names.
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
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
