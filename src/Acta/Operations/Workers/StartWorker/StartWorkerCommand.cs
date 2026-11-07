namespace Acta;

/// <summary>
/// Validated worker bootstrap; construct via <see cref="Create"/> so the namespace canonicalization is computed once,
/// identically for every caller. <c>RequiresDeployment</c> false admits the worker whatever versions have deployed in
/// the namespace.
/// </summary>
internal sealed record StartWorkerCommand(
    string NamespaceName,
    string HostName,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int ProcessId,
    int MaxConcurrentExecutors,
    Guid WorkerRef,
    bool RequiresDeployment
)
{
    public static StartWorkerCommand Create(
        string namespaceName,
        string hostName,
        string deploymentVersion,
        string? engineVersion,
        string? dotnetVersion,
        int processId,
        int maxConcurrentExecutors,
        Guid workerRef,
        bool requiresDeployment
    ) =>
        new(
            NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true),
            hostName,
            deploymentVersion,
            engineVersion,
            dotnetVersion,
            processId,
            maxConcurrentExecutors,
            workerRef,
            requiresDeployment
        );
}
