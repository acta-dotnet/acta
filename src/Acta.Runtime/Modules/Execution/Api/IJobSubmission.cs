namespace Acta.Runtime.Modules.Execution.Api;

/// <summary>
/// Execution's owned batch-submission seam: the owned batch enqueue path (with worker wake). Edge
/// consumers (the outbox relay) depend only on this, never on job internals, while
/// <see cref="JobsSubmission"/> forwards to the normal owned <c>IJobs.EnqueueBatchAsync</c> path
/// (not the transactional twin).
/// </summary>
internal interface IJobSubmission
{
    ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct);
}

/// <summary>Forwards submissions to the owned <c>IJobs.EnqueueBatchAsync</c> path.</summary>
internal sealed class JobsSubmission(IJobs jobs) : IJobSubmission
{
    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct) =>
        jobs.EnqueueBatchAsync(requests, ct);
}
