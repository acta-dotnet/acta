using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Plain-English explanation of a Job's current durable state: where the work is, why it moved there, and what an
/// operator can do next, assembled from the same <c>jobs_state</c>, <c>workers</c>, <c>steps</c>, <c>checkpoints</c>, and
/// <c>events</c> rows a <c>SELECT</c> would read. Returned by <see cref="IJobs.ExplainAsync"/>; the CLI renders it as
/// prose, the dashboard as a panel. <c>Headline</c> is a one-sentence summary and <c>StatusMeaning</c> the
/// <c>[Code]</c> description of <c>Status</c>; <c>ActiveWait</c> and <c>Lease</c> are set only in the relevant states,
/// <c>LastExecutedBy</c> names the last worker when no lease is live, and <c>NextActions</c> are the operator's moves.
/// </summary>
public sealed record GetJobExplanationResponse(
    [property: JsonIgnore] long JobId,
    JobRef JobRef,
    string JobNamespace,
    string JobName,
    JobStatusCode Status,
    string StatusMeaning,
    string Headline,
    GetJobExplanationWait? ActiveWait,
    GetJobExplanationLease? Lease,
    string? LastExecutedBy,
    IReadOnlyList<GetJobExplanationStep> Steps,
    string? Reason,
    IReadOnlyList<GetJobExplanationAction> NextActions
);
