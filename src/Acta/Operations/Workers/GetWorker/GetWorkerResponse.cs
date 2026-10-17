using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Durable operator detail for one worker process registration.</summary>
public sealed record GetWorkerResponse(
    WorkerRef WorkerRef,
    [property: JsonIgnore] int WorkerId,
    string JobNamespace,
    WorkerStatusCode Status,
    string Host,
    string DeploymentVersion,
    string? EngineVersion,
    string? DotnetVersion,
    int? ProcessId,
    int MaxConcurrency,
    DateTime LastSeenAtUtc,
    DateTime StartedAtUtc,
    DateTime ModifiedAtUtc
);
