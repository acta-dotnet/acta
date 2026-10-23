namespace Acta;

/// <summary>
/// Validated worker bootstrap; construct via <see cref="Create"/> so the namespace canonicalization is computed once,
/// identically for every caller.
/// </summary>
internal sealed record StartWorkerCommand(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    string HostName,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int ProcessId,
    int MaxConcurrentExecutors,
    Guid WorkerRef
)
{
    public static StartWorkerCommand Create(
        string namespaceName,
        string? ownerTeam,
        string? description,
        string hostName,
        string deploymentVersion,
        string? engineVersion,
        string? dotnetVersion,
        int processId,
        int maxConcurrentExecutors,
        Guid workerRef
    ) =>
        new(
            NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true),
            ownerTeam,
            description,
            hostName,
            deploymentVersion,
            engineVersion,
            dotnetVersion,
            processId,
            maxConcurrentExecutors,
            workerRef
        );
}
