namespace Acta;

/// <summary>Durable operator detail for one worker process registration.</summary>
public sealed record JobWorkerDetail(
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
    DateTime StartedAtUtc,
    DateTime ModifiedAtUtc
);
