using System.Text.Json.Serialization;

namespace Acta;

/// <summary>One namespace. An active namespace accepts enqueues and a suspended one rejects them.</summary>
/// <param name="NamespaceId">The namespace's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The namespace's name.</param>
/// <param name="Status">The namespace's status.</param>
/// <param name="OwnerTeam">The team that owns the namespace, when one is recorded.</param>
/// <param name="Description">The namespace's description, when one is recorded.</param>
/// <param name="Version">The namespace's version, which every change to it increments; the namespace update takes it as
/// expectedVersion.</param>
public sealed record GetNamespaceResponse(
    [property: JsonIgnore] int NamespaceId,
    string JobNamespace,
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    int Version
);
