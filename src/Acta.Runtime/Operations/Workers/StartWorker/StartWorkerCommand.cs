namespace Acta.Runtime;

/// <summary>
/// Validated worker bootstrap; construct via <see cref="Create"/> so the namespace canonicalization
/// and catalog-hash gate value are computed once, identically for every caller.
/// </summary>
internal sealed record StartWorkerCommand(
    string NamespaceName,
    string? OwnerTeam,
    string? Description,
    string CatalogHash,
    string HostName,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int ProcessId,
    int MaxConcurrency,
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
        int maxConcurrency,
        Guid workerRef
    ) =>
        new(
            IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName)),
            ownerTeam,
            description,
            Acta.Runtime.CatalogHash.Of(ownerTeam, description),
            hostName,
            deploymentVersion,
            engineVersion,
            dotnetVersion,
            processId,
            maxConcurrency,
            workerRef
        );
}
