using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Durable operator detail for one worker process registration.</summary>
/// <param name="WorkerId">The worker's internal id; the JSON omits it and carries workerRef.</param>
/// <param name="WorkerRef">The worker's ref, wrk_ followed by 32 hex digits.</param>
/// <param name="JobNamespace">The namespace the worker runs jobs for.</param>
/// <param name="Status">The worker's status.</param>
/// <param name="HostName">The name of the machine the worker runs on.</param>
/// <param name="DeploymentVersion">The version of the application the worker runs, as the host named it.</param>
/// <param name="EngineVersion">The version of Acta the worker runs, when it reported one.</param>
/// <param name="DotnetVersion">The .NET runtime version the worker runs on, when it reported one.</param>
/// <param name="ProcessId">The worker's operating-system process id, when it reported one.</param>
/// <param name="MaxConcurrentExecutors">How many jobs the worker runs at once.</param>
/// <param name="LastSeenAtUtc">When the worker last sent a heartbeat, in UTC.</param>
/// <param name="StartedAtUtc">When the worker started, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
public sealed record GetWorkerResponse(
    WorkerRef WorkerRef,
    [property: JsonIgnore] int WorkerId,
    string JobNamespace,
    WorkerStatusCode Status,
    string HostName,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int? ProcessId,
    int MaxConcurrentExecutors,
    DateTime LastSeenAtUtc,
    DateTime StartedAtUtc,
    DateTime ModifiedAtUtc
);
