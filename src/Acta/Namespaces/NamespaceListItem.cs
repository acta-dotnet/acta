using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One namespace row in an <see cref="INamespaces.ListAsync"/> page, projected from the
/// <c>namespaces</c> table, and the one representation <c>GET /namespaces</c> answers with.
/// <see cref="INamespaces.ListNamesAsync"/> remains for callers that want only the names.
/// </summary>
/// <param name="NamespaceId">DB-assigned namespace id, referenced by <c>job.namespace_id</c>; internal, never serialized (a namespace is addressed by its name, and the seeded system one is recognized by <see cref="IdentifierSyntax.ReservedSystemName"/>).</param>
/// <param name="JobNamespace">Operator-readable kebab-case namespace name. Unique.</param>
/// <param name="Status">Namespace lifecycle status (Active resolves at enqueue; Suspended rejects it).</param>
/// <param name="OwnerTeam">Owning-team identifier surfaced on dashboards, or null.</param>
/// <param name="Description">Operator-readable description, or null.</param>
/// <param name="Version">Optimistic-concurrency row version; pass as the expected version to a CAS control verb.</param>
public sealed record NamespaceListItem(
    [property: JsonIgnore] int NamespaceId,
    string JobNamespace,
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
