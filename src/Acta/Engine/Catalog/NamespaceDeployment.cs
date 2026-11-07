namespace Acta;

/// <summary>
/// What a deploy did in one namespace: the version it ran as, the environment that gated its schedules, and either the
/// definitions it added, changed, and left unchanged with the stored Active ones the build no longer declares, or, for
/// an older build, the version the namespace stays at.
/// </summary>
internal sealed record NamespaceDeployment(
    string NamespaceName,
    string DeploymentVersion,
    string? EnvironmentName,
    string? CurrentDeploymentVersion,
    bool IsOlder,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed,
    int Unchanged,
    IReadOnlyList<string> Undeclared
);
