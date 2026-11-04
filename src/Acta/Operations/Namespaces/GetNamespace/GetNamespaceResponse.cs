using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single-namespace read model returned by <see cref="INamespaces.GetAsync"/>. It carries the same fields as
/// <see cref="ListNamespacesItem"/> today and is a distinct type because a detail read can grow additively without the
/// list row paying for it. <c>NamespaceId</c> is the DB-assigned id <c>jobs.namespace_id</c> references, internal and
/// never serialized, since a namespace is addressed by its unique kebab-case name; <c>Status</c> Active resolves at
/// enqueue and Suspended rejects it; <c>Version</c> is the row version a CAS control verb takes as expected.
/// </summary>
public sealed record GetNamespaceResponse(
    [property: JsonIgnore] int NamespaceId,
    string JobNamespace,
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
