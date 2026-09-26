using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Recursively cancels a job's non-terminal descendant subtree, top-down, one single-row
/// <c>cancel_job</c> call per node with the caller's reason. A composite over <c>GetChildJobIds</c>
/// and <c>CancelJob</c> with no SQL of its own, multi-transaction by design (cancel is an exceptional
/// verb): a crash mid-walk leaves repairable stragglers, and re-running the cancel resumes the walk
/// because every child is recursed through, terminal ones included, so live descendants behind an
/// already-finished child are still reached. In-subtree latch raises are skipped: each node's parent
/// is terminal by the time the node is cancelled. Returns the ids the walk cancelled, for the
/// caller's completion wakes, and whether any of its cancels promoted a lane member, for its work wake.
/// </summary>
internal static class CancelDescendants
{
    /// <summary>
    /// The cascade an ancestor's own cancellation causes. Named rather than defaulted so the other
    /// cause, a parent whose bounded child wait expired, has to say which one it is.
    /// </summary>
    public static readonly JobControlInput ParentCancelled = new(
        new JobControlActor(ActorCode.Sys),
        JobEventReasonCode.JobParentCancelled,
        "Ancestor job cancelled."
    );

    public static async Task<CancelledDescendants> Run(
        IExecutionStore execution,
        IJobStore store,
        long rootJobId,
        JobControlInput input,
        CancellationToken ct
    )
    {
        var cancelled = new List<long>();
        var promoted = await WalkAsync(execution, store, rootJobId, input, cancelled, ct);
        return new CancelledDescendants(cancelled, promoted);
    }

    private static async Task<bool> WalkAsync(
        IExecutionStore execution,
        IJobStore store,
        long parentJobId,
        JobControlInput input,
        List<long> cancelled,
        CancellationToken ct
    )
    {
        var promoted = false;
        foreach (var childId in await execution.GetChildJobIdsAsync(parentJobId, ct))
        {
            var cancel = await store.CancelJobAsync(childId, input, ct);
            if (cancel.Outcome.Action == JobControlActionInternal.Applied)
            {
                cancelled.Add(childId);
                promoted |= cancel.LanePromoted;
            }

            promoted |= await WalkAsync(execution, store, childId, input, cancelled, ct);
        }

        return promoted;
    }
}

/// <summary>What a descendant cancel walk did: the ids it cancelled, and whether it promoted a lane member.</summary>
internal sealed record CancelledDescendants(IReadOnlyList<long> Cancelled, bool LanePromoted);
