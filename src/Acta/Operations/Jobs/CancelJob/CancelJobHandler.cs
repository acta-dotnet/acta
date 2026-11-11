using System.Data.Common;

namespace Acta;

/// <summary>
/// <see cref="IJobs.CancelAsync"/>: cancels the job, raises its child latch on the parent, then cancels the
/// non-terminal descendants. The engine cancels by id through the same routine.
/// </summary>
internal sealed class CancelJobHandler(
    GetJobIdHandler getJobId,
    IDbSession session,
    ISqlDialect dialect,
    ExecutionOperations execution,
    SignalsOperations signals,
    WorkerWakeupPublisher wakeup
)
{
    /// <summary>
    /// The cascade an ancestor's own cancellation causes. Named rather than defaulted so the other cause, a
    /// parent whose bounded child wait expired, has to say which one it is.
    /// </summary>
    public static readonly JobControlInput ParentCancelled = new(null, EventReason.ParentCancelled, "Ancestor job cancelled.");

    public async ValueTask<CancelJobResponse> HandleAsync(CancelJobCommand command, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new CancelJobResponse(0, ControlAction.NotFound, null, null);
        }

        var row = await CancelAsync(
            jobId,
            JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion),
            fence: null,
            walkRootJobId: null,
            ct
        );

        // What follows the commit does not take the caller's token: a caller that stops waiting, as a closed dashboard
        // tab does, leaves the latch, the wake and the walk running to the end.
        await FollowUpAsync(jobId, row).WaitAsync(ct);
        return new CancelJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    private async Task FollowUpAsync(long jobId, CancelJobRow row)
    {
        if (row.Action == ControlAction.Applied)
        {
            var released =
                row.ParentJobId is { } parentId
                && await RaiseChildLatch.Run(signals, jobId, parentId, JobStatusCode.Cancelled, CancellationToken.None);
            await JobControl.WakeWorkersIfAsync(wakeup, released || row.LanePromoted, CancellationToken.None);
        }

        // An already-cancelled job walks too, so cancelling it again resumes a walk a crash cut short.
        if (row.Status == JobStatusCode.Cancelled)
        {
            await CancelDescendantsAsync(jobId, ParentCancelled, CancellationToken.None);
        }
    }

    /// <summary>
    /// One <c>cancel_job</c> call by id that wakes the job's waiters when it applied: no lookup, no latch, no worker wake.
    /// A <paramref name="fence"/> holds it to an ancestor's attempt, and a refusal throws <see cref="ExecutionFencedException"/>.
    /// A <paramref name="walkRootJobId"/> holds it to that job still being Cancelled, and a refusal throws the provider's
    /// error carrying <see cref="WalkEndedToken"/>.
    /// </summary>
    internal async Task<CancelJobRow> CancelAsync(
        long jobId,
        JobControlInput input,
        ExecutionFence? fence,
        long? walkRootJobId,
        CancellationToken ct
    )
    {
        var row = await ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("CancelJob"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FenceJobId, fence?.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FenceExecutionNumber, fence?.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FenceWorkerId, fence?.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WalkRootJobId, walkRootJobId));
                    JobControl.BindEvent(cmd, dialect, input);
                    JobControl.BindExpectedVersion(cmd, dialect, input);
                },
                DbProjectionResolver.Resolve<CancelJobRow>(),
                ct
            )
        );
        if (row.Action == ControlAction.Applied)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.JobCompletion(jobId), WorkerWakeupReason.JobFinished, ct);
        }

        return row;
    }

    /// <summary>
    /// <see cref="JobsOperations.CancelTreeAsync"/>: the root's cancel, then the descendant walk, every node under
    /// <paramref name="fence"/>, so an attempt that lost its job stops at the next node. The fence is the walk's only
    /// guard: the walk runs once, inside that attempt, and a root that finished just before its cancel still has its
    /// live descendants cancelled.
    /// </summary>
    internal async Task CancelTreeAsync(long rootJobId, JobControlInput input, ExecutionFence fence, CancellationToken ct)
    {
        var root = await CancelAsync(rootJobId, input, fence, walkRootJobId: null, ct);
        var promoted = root.Action == ControlAction.Applied && root.LanePromoted;
        var walk = await WalkAsync(walkRootJobId: null, rootJobId, input, fence, ct);
        await JobControl.WakeWorkersIfAsync(wakeup, walk.Promoted || promoted, ct);
    }

    /// <summary>
    /// Cancels the non-terminal descendant subtree top-down, one transaction per node: a crash mid-walk leaves
    /// repairable stragglers, and terminal children are recursed through too, so a re-run reaches live
    /// descendants behind them. In-subtree latches are skipped, since each node's parent is already terminal.
    /// Each cancel lands only while the root is still Cancelled, so a walk that outlives a restart of the root ends
    /// there and leaves the restarted job's new children alone. Wakes every worker namespace once if a cancel
    /// promoted a lane member.
    /// </summary>
    internal async Task CancelDescendantsAsync(long rootJobId, JobControlInput input, CancellationToken ct) =>
        await JobControl.WakeWorkersIfAsync(wakeup, (await WalkAsync(rootJobId, rootJobId, input, fence: null, ct)).Promoted, ct);

    /// <summary>The token <c>cancel_job</c> raises when a walk's root is no longer Cancelled.</summary>
    internal const string WalkEndedToken = "ACTA:CANCEL_WALK_ENDED:";

    private async Task<(bool Promoted, bool Ended)> WalkAsync(
        long? walkRootJobId,
        long parentJobId,
        JobControlInput input,
        ExecutionFence? fence,
        CancellationToken ct
    )
    {
        var promoted = false;
        foreach (var childId in (await execution.ListChildJobIdsAsync(parentJobId, ct)).ChildJobIds)
        {
            CancelJobRow row;
            try
            {
                row = await CancelAsync(childId, input, fence, walkRootJobId, ct);
            }
            catch (DbException ex) when (ex.Message.Contains(WalkEndedToken, StringComparison.Ordinal))
            {
                return (promoted, true);
            }

            promoted |= row.Action == ControlAction.Applied && row.LanePromoted;
            var below = await WalkAsync(walkRootJobId, childId, input, fence, ct);
            promoted |= below.Promoted;
            if (below.Ended)
            {
                return (promoted, true);
            }
        }

        return (promoted, false);
    }
}
