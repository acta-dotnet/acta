namespace Acta;

/// <summary><see cref="IJobs"/> plus the engine's calls into the group: the system actor's cancels and the durable-state reset.</summary>
internal interface IJobsOperations : IJobs
{
    /// <summary>
    /// Cancels the non-terminal descendants of a job its own handler cancelled, stamped as an ancestor's cancellation by
    /// the system actor. Wakes every worker namespace once if a cancel promoted a lane member.
    /// </summary>
    Task CancelDescendantsAsync(long jobId, CancellationToken ct);

    /// <summary>
    /// Cancels a job and then its descendants under one input, waking every worker namespace once if any cancel
    /// promoted a lane member: a parent abandoning a child whose bounded wait expired.
    /// </summary>
    Task CancelTreeAsync(long rootJobId, JobControlInput input, CancellationToken ct);

    /// <summary>Clears a job's durable step and checkpoint state ahead of a fresh attempt, behind <c>ctx.ResetStateAsync</c>.</summary>
    Task ResetJobStateAsync(long jobId, CancellationToken ct);
}
