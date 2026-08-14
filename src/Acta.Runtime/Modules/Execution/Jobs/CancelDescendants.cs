using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Recursively cancels a job's non-terminal descendant subtree, top-down, one single-row
/// <c>cancel_job</c> call per node with reason <c>ParentCancelled</c>. A composite over
/// <c>GetChildJobIds</c> and <c>CancelJob</c> with no SQL of its own, multi-transaction by design
/// (cancel is an exceptional verb): a crash mid-walk leaves repairable stragglers, and re-running
/// the cancel resumes the walk because every child is recursed through, terminal ones included, so
/// live descendants behind an already-finished child are still reached. In-subtree latch raises are
/// skipped: each node's parent is terminal by the time the node is cancelled. Returns the ids the
/// walk cancelled, for the caller's completion wakes.
/// </summary>
internal static class CancelDescendants
{
    public static async Task<IReadOnlyList<long>> Run(IExecutionStore execution, IJobStore store, long rootJobId, CancellationToken ct)
    {
        var cancelled = new List<long>();
        await WalkAsync(execution, store, rootJobId, cancelled, ct);
        return cancelled;
    }

    private static async Task WalkAsync(
        IExecutionStore execution,
        IJobStore store,
        long parentJobId,
        List<long> cancelled,
        CancellationToken ct
    )
    {
        var input = new JobControlInput(
            new JobControlActor(ActorCode.Sys),
            JobEventReasonCode.JobParentCancelled,
            "Ancestor job cancelled."
        );

        foreach (var childId in await execution.GetChildJobIdsAsync(parentJobId, ct))
        {
            var cancel = await store.CancelJobAsync(childId, input, ct);
            if (cancel.Outcome.Action == JobControlActionInternal.Applied)
            {
                cancelled.Add(childId);
            }

            await WalkAsync(execution, store, childId, cancelled, ct);
        }
    }
}
