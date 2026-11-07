using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Plain-English explanation of a Job's current durable state: where the work is, why it moved there, and what an
/// operator can do next, assembled from the same <c>jobs_state</c>, <c>workers</c>, <c>steps</c>, <c>checkpoints</c>, and
/// <c>events</c> rows a <c>SELECT</c> would read. Returned by <see cref="IJobs.ExplainAsync"/>; the CLI renders it as
/// prose, the dashboard as a panel. <c>StatusMeaning</c> is the <c>[Code]</c> description of <c>Status</c>; <c>ActiveWait</c>
/// and <c>Lease</c> are set only in their states, <c>LastExecutedBy</c> names the last worker when no lease is live, and
/// <c>Steps</c> are the newest <see cref="ExplainJobHandler.StepLimit"/>, <c>StepsHasMore</c> saying older ones exist.
/// </summary>
/// <param name="JobId">The job's internal id; the JSON omits it and carries jobRef.</param>
/// <param name="JobRef">The job's ref, job_ followed by 32 hex digits.</param>
/// <param name="JobNamespace">The namespace of the job's definition.</param>
/// <param name="JobName">The name of the job's definition.</param>
/// <param name="Status">The job's status.</param>
/// <param name="StatusMeaning">What the job's status means, in a sentence.</param>
/// <param name="Headline">A one-sentence summary of where the job is and why.</param>
/// <param name="ActiveWait">The wait the job is suspended on; null when it is not waiting.</param>
/// <param name="Lease">The lease on the job; null when no worker holds one.</param>
/// <param name="LastExecutedBy">The name of the worker that last ran the job, when no lease is live.</param>
/// <param name="Steps">The job's newest steps, oldest first; stepsHasMore says when older ones were left out.</param>
/// <param name="StepsHasMore">Whether the job has older steps than the ones listed.</param>
/// <param name="Reason">The latest reason recorded for the job's state, when there is one.</param>
/// <param name="NextActions">The moves an operator can make from here.</param>
public sealed record ExplainJobResponse(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    string StatusMeaning,
    string Headline,
    ExplainJobWait? ActiveWait,
    ExplainJobLease? Lease,
    string? LastExecutedBy,
    IReadOnlyList<ExplainJobStep> Steps,
    bool StepsHasMore,
    string? Reason,
    IReadOnlyList<ExplainJobAction> NextActions
);
