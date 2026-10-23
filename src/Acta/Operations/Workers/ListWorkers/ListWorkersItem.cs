using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One worker row in a <see cref="IWorkers.ListAsync"/> page: the registration's public ref and internal id, its
/// namespace and status, and what it reported at start (host machine, deployment version, engine version, runtime
/// framework, process id, executor cap), with the last heartbeat, start, and last change instants.
/// </summary>
public sealed record ListWorkersItem(
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
