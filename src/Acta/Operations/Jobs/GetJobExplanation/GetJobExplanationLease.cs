using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The execution lease on an Executing Job and the liveness of the worker holding it. When
/// <see cref="Expired"/> is true the lease has lapsed and <c>sys.recovery</c> reclaims the Job on its next tick;
/// <see cref="RecoveryExpectation"/> says so in prose. <see cref="WorkerName"/> is the worker's deployment version, and
/// <see cref="WorkerStale"/> is true once it has missed heartbeats past <c>JobsOptions.WorkerDeadAfter</c>. Both
/// <see cref="WorkerRef"/> and <see cref="WorkerName"/> go null when retention purged the owner's row: the timings
/// still describe the Job, but the holder is unidentifiable.
/// </summary>
public sealed record GetJobExplanationLease(
    [property: JsonIgnore] int WorkerId,
    string? WorkerName,
    DateTime? ExpiresAtUtc,
    bool Expired,
    DateTime? WorkerLastHeartbeatAtUtc,
    bool WorkerStale,
    string RecoveryExpectation,
    WorkerRef? WorkerRef
);
