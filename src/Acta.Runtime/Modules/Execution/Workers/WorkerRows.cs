namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// The namespace and worker ids the bootstrap routine assigns - the single-row result of the start.
/// </summary>
internal readonly record struct StartWorkerRow(int NamespaceId, int WorkerId);

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
    DateTime LastHeartbeatAtUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    Guid WorkerRef
)
{
    public WorkerListItem ToItem() =>
        new(
            new Acta.WorkerRef(WorkerRef),
            WorkerId,
            JobNamespace,
            Status,
            Host,
            DeploymentVersion,
            EngineVersion,
            DotnetVersion,
            ProcessId,
            MaxConcurrency,
            LastHeartbeatAtUtc,
            CreatedAtUtc,
            ModifiedAtUtc
        );

    public WorkerDetail ToDetail() =>
        new(
            new Acta.WorkerRef(WorkerRef),
            WorkerId,
            JobNamespace,
            Status,
            Host,
            DeploymentVersion,
            EngineVersion,
            DotnetVersion,
            ProcessId,
            MaxConcurrency,
            LastHeartbeatAtUtc,
            CreatedAtUtc,
            ModifiedAtUtc
        );
}
