using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One namespace row in an <see cref="INamespaces.ListAsync"/> page, projected from the <c>namespaces</c> table and the
/// one representation <c>GET /namespaces</c> answers with. <c>NamespaceId</c> is the DB-assigned id
/// <c>job.namespace_id</c> references, internal and never serialized, since a namespace is addressed by its unique
/// kebab-case name; <c>Status</c> Active resolves at enqueue and Suspended rejects it; <c>Version</c> is the row
/// version a CAS control verb takes as expected.
/// </summary>
/// <param name="NamespaceId">The namespace's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The namespace's name.</param>
/// <param name="Status">The namespace's status.</param>
/// <param name="OwnerTeam">The team that owns the namespace, when one is recorded.</param>
/// <param name="Description">The namespace's description, when one is recorded.</param>
/// <param name="Version">The namespace's version, which every change to it increments; send it as the expected version of a
/// control.</param>
public sealed record ListNamespacesItem(
    [property: JsonIgnore] int NamespaceId,
    string JobNamespace,
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
