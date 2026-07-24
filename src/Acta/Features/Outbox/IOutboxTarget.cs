namespace Acta.Features.Outbox;

/// <summary>
/// The relay's target-ingestion seam: the owned batch enqueue path (with worker wake). Kept narrow so
/// the relay policy depends only on the batch enqueue it needs, while <see cref="JobsOutboxTarget"/>
/// forwards to the normal owned <c>IJobs.EnqueueBatchAsync</c> path (not the transactional twin).
/// </summary>
internal interface IOutboxTarget
{
    ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct);
}

/// <summary>Forwards relay ingestion to the owned <c>IJobs.EnqueueBatchAsync</c> path.</summary>
internal sealed class JobsOutboxTarget(IJobs jobs) : IOutboxTarget
{
    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct) =>
        jobs.EnqueueBatchAsync(requests, ct);
}
