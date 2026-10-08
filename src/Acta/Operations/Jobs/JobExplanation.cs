using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Plain-English explanation of a Job's current durable state: where the work is, why it moved there, and what an
/// operator can do next, assembled from the same <c>runtimes</c>, <c>workers</c>, <c>steps</c>, <c>checkpoints</c>, and
/// <c>events</c> rows a <c>SELECT</c> would read. Returned by <see cref="IJobs.ExplainAsync"/>; the CLI renders it as
/// prose, the dashboard as a panel. <c>Headline</c> is a one-sentence summary and <c>StatusMeaning</c> the
/// <c>[Code]</c> description of <c>Status</c>; <c>ActiveWait</c> and <c>Lease</c> are set only in the relevant states,
/// <c>LastExecutedBy</c> names the last worker when no lease is live, and <c>NextActions</c> are the operator's moves.
/// </summary>
public sealed record JobExplanation(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    string StatusMeaning,
    string Headline,
    JobExplainWait? ActiveWait,
    JobExplainLease? Lease,
    string? LastExecutedBy,
    IReadOnlyList<JobExplainStep> Steps,
    string? Reason,
    IReadOnlyList<JobExplainAction> NextActions
);

/// <summary>The durable wait a Suspended Job is blocked on: a signal name, or a timer slot and its due instant.</summary>
public sealed record JobExplainWait(CheckpointKindCode Kind, string Name, DateTime? DueAtUtc);

/// <summary>
/// The execution lease on a Dispatched or Executing Job and the liveness of the worker holding it. When
/// <see cref="Expired"/> is true the lease has lapsed and <c>sys.recovery</c> reclaims the Job on its next tick;
/// <see cref="RecoveryExpectation"/> says so in prose. <see cref="WorkerName"/> is the worker's deployment version, and
/// <see cref="WorkerStale"/> is true once it has missed heartbeats past <c>JobsOptions.WorkerDeadAfter</c>. Both
/// <see cref="WorkerRef"/> and <see cref="WorkerName"/> go null when retention purged the owner's row: the timings
/// still describe the Job, but the holder is unidentifiable.
/// </summary>
public sealed record JobExplainLease(
    [property: JsonIgnore] int WorkerId,
    string? WorkerName,
    DateTime? ExpiresAtUtc,
    bool Expired,
    DateTime? WorkerLastHeartbeatAtUtc,
    bool WorkerStale,
    string RecoveryExpectation,
    WorkerRef? WorkerRef
);

/// <summary>One step slot's state, with <see cref="Explanation"/> rendering it as a plain-English clause.</summary>
public sealed record JobExplainStep(string Name, StepStatusCode Status, string Explanation);

/// <summary>
/// A suggested operator move. <see cref="Kind"/> is a stable slug
/// (<c>raise-signal</c> | <c>cancel</c> | <c>resume</c> | <c>restart</c> | <c>inspect-timeline</c> |
/// <c>wait-recovery</c> | <c>view-result</c> | <c>none</c>); <see cref="Description"/> is the prose shown to the operator.
/// </summary>
public sealed record JobExplainAction(string Kind, string Description);
