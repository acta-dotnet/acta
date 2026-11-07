using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The execution lease on an Executing Job and the liveness of the worker holding it. When
/// <see cref="Expired"/> is true the lease has lapsed and crash recovery reclaims the Job on its next pass;
/// <see cref="RecoveryExpectation"/> says so in prose. <see cref="WorkerName"/> is the worker's deployment version, and
/// <see cref="WorkerStale"/> is true once it has missed heartbeats past <c>JobsOptions.WorkerDeadAfter</c>. Both
/// <see cref="WorkerRef"/> and <see cref="WorkerName"/> go null when retention purged the owner's row: the timings
/// still describe the Job, but the holder is unidentifiable.
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
