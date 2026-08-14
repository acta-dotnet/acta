namespace Acta;

/// <summary>
/// One worker row in a <see cref="IWorkers.ListAsync"/> page.
/// </summary>
/// <param name="WorkerId">Worker row id.</param> <param name="JobNamespace">Namespace the worker executes for.</param> <param name="Status">Current worker status.</param>
/// <param name="Host">Machine name reported at start.</param>
/// <param name="DeploymentVersion">Deployment version reported at start.</param>
/// <param name="EngineVersion">Acta engine assembly version reported at start.</param>
/// <param name="DotnetVersion">Runtime framework description reported at start (e.g. ".NET 10.0.0").</param>
/// <param name="ProcessId">OS process id of the worker process.</param>
/// <param name="MaxConcurrency">Effective per-process executor cap reported at start.</param>
/// <param name="LastSeenAtUtc">Last heartbeat instant.</param> <param name="CreatedAtUtc">Worker start instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param>
public sealed record WorkerListItem(
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
);
