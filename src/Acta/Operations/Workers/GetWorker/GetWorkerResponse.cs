using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Durable operator detail for one worker process registration.</summary>
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
