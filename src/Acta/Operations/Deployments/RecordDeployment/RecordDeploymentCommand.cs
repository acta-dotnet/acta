using System.Data.Common;

namespace Acta;

/// <summary>
/// The version a deploy landed in a namespace, with the namespace's declared owner and description and what the
/// <c>namespace.deployed</c> event says of it: the version it replaced, whether the deploy was forced, and the jobs it
/// added and changed. A null version records nothing and writes the owner and description alone: what a start under
/// <c>MigrateOnStartup</c> leaves.
/// </summary>
internal sealed record RecordDeploymentCommand(
    DbTransaction Transaction,
    int NamespaceId,
    string? DeploymentVersion,
    string? OwnerTeam,
    string? Description,
    string? PreviousDeploymentVersion,
    bool Forced,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Changed
);
