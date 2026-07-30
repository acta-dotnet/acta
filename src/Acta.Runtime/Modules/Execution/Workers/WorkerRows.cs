namespace Acta.Modules.Execution.Workers;

/// <summary>
/// The namespace and worker ids the bootstrap routine assigns - the single-row result of the start.
/// </summary>
internal readonly record struct StartWorkerRow(short NamespaceId, int WorkerId);

/// <summary>
/// One <c>workers</c> row projected for the workers list read.
/// </summary>
internal sealed record JobWorkerListRow(
    int WorkerId,
    string JobNamespace,
    WorkerStatusCode Status,
    string Host,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int? ProcessId,
    int MaxConcurrency,
    DateTime LastSeenAtUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc
)
{
    public JobWorkerListItem ToItem() =>
        new(
            WorkerId,
            JobNamespace,
            Status,
            Host,
            DeploymentVersion,
            EngineVersion,
            DotnetVersion,
            ProcessId,
            MaxConcurrency,
            LastSeenAtUtc,
            CreatedAtUtc,
            ModifiedAtUtc
        );

    public JobWorkerDetail ToDetail() =>
        new(
            WorkerId,
            JobNamespace,
            Status,
            Host,
            DeploymentVersion,
            EngineVersion,
            DotnetVersion,
            ProcessId,
            MaxConcurrency,
            LastSeenAtUtc,
            CreatedAtUtc,
            ModifiedAtUtc
        );
}
