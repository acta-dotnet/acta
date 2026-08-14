using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Plain-English explanation of a Job's current durable state: where the work is, why it moved there,
/// and what an operator can do next. Assembled from the same <c>runtimes</c> / <c>workers</c> /
/// <c>steps</c> / <c>checkpoints</c> / <c>events</c> rows an operator could read with a <c>SELECT</c>:
/// Acta explains the work it runs rather than hiding it behind synthesized state. Returned by
/// <see cref="IJobs.ExplainAsync"/>; the CLI renders it as prose, the dashboard as a panel.
/// <para><c>StatusMeaning</c> is the <c>[Code]</c> description of <c>Status</c>; <c>Headline</c> is a
/// one-sentence summary; <c>ActiveWait</c> / <c>Lease</c> are set only in the relevant states;
/// <c>LastExecutedBy</c> names the worker that last ran the job (for states with no live lease);
/// <c>Reason</c> is the most recent reason on the timeline; <c>NextActions</c> are the operator's
/// moves, most relevant first.</para>
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
public sealed record JobExplainWait(JobCheckpointKindCode Kind, string Name, DateTime? DueAtUtc);

/// <summary>
/// The execution lease on a Dispatched / Executing Job and the liveness of the worker that holds it.
/// When <see cref="Expired"/> is true the lease has lapsed and <c>sys.recovery</c> reclaims the Job on
/// its next maintenance tick; <see cref="RecoveryExpectation"/> states that in prose.
/// <see cref="WorkerName"/> is the worker's deployment version; <see cref="WorkerStale"/> is true once
/// the worker has missed heartbeats past <c>JobsOptions.WorkerDeadAfter</c>.
/// </summary>
public sealed record JobExplainLease(
    int WorkerId,
    string? WorkerName,
    DateTime? ExpiresAtUtc,
    bool Expired,
    DateTime? WorkerLastHeartbeatAtUtc,
    bool WorkerStale,
    string RecoveryExpectation
);

/// <summary>One step slot's state, with <see cref="Explanation"/> rendering it as a plain-English clause.</summary>
public sealed record JobExplainStep(string Name, JobStepStatusCode Status, string Explanation);

/// <summary>
/// A suggested operator move. <see cref="Kind"/> is a stable slug
/// (<c>raise-signal</c> | <c>cancel</c> | <c>resume</c> | <c>restart</c> | <c>inspect-timeline</c> |
/// <c>wait-recovery</c> | <c>view-result</c> | <c>none</c>); <see cref="Description"/> is the prose shown to the operator.
/// </summary>
public sealed record JobExplainAction(string Kind, string Description);
