using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The lease on an executing job, and whether the worker that holds it is alive. Once the lease has expired, recovery reclaims the job on
/// its next pass. The worker's ref and name are null once retention has removed the worker.
/// </summary>
/// <param name="WorkerId">The internal id of the worker holding the lease; the JSON omits it and carries workerRef.</param>
/// <param name="WorkerName">The name of the worker that holds the lease, when it is known.</param>
/// <param name="ExpiresAtUtc">When the lease expires, in UTC.</param>
/// <param name="Expired">Whether the lease has expired.</param>
/// <param name="WorkerLastSeenAtUtc">When the worker last sent a heartbeat, in UTC.</param>
/// <param name="WorkerStale">Whether the worker has stopped sending heartbeats.</param>
/// <param name="RecoveryExpectation">What recovery will do with the job, in a sentence.</param>
/// <param name="WorkerRef">The ref of the worker that holds the lease.</param>
public sealed record ExplainJobLease(
    [property: JsonIgnore] int WorkerId,
    string? WorkerName,
    DateTime? ExpiresAtUtc,
    bool Expired,
    DateTime? WorkerLastSeenAtUtc,
    bool WorkerStale,
    string RecoveryExpectation,
    WorkerRef? WorkerRef
);
