using System.Text.Json.Serialization;

namespace Acta;

/// <summary>One namespace in a page.</summary>
/// <param name="NamespaceId">The namespace's internal id; the JSON omits it.</param>
/// <param name="JobNamespace">The namespace's name.</param>
/// <param name="Status">The namespace's status.</param>
/// <param name="OwnerTeam">The team that owns the namespace, when one is recorded.</param>
/// <param name="Description">The namespace's description, when one is recorded.</param>
/// <param name="DeploymentVersion">The app version currently deployed in the namespace; null when no deploy has recorded one.</param>
/// <param name="Version">The namespace's version, which every change to it increments; the namespace update takes it as
/// expectedVersion.</param>
public sealed record ListNamespacesItem(
    [property: JsonIgnore] int NamespaceId,
    string JobNamespace,
    NamespaceStatusCode Status,
    string? OwnerTeam,
    string? Description,
    string? DeploymentVersion,
    long Version
);
