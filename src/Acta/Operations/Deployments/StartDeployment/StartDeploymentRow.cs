namespace Acta;

/// <summary>
/// One namespace of a deploy: its id, the version it currently stands at (null when no deploy has landed), and whether
/// the deploying version has landed there before.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record StartDeploymentRow(string NamespaceName, int NamespaceId, string? CurrentDeploymentVersion, bool IsDeployed);
