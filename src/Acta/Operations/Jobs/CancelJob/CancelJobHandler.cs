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
                && await RaiseChildLatch.Run(
                    signals,
                    jobId,
                    parentId,
                    JobStatusCode.Cancelled,
                    childExecutionNumber: null,
                    CancellationToken.None
                );
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
    /// </summary>
    internal async Task<CancelJobRow> CancelAsync(long jobId, JobControlInput input, ExecutionFence? fence, CancellationToken ct)
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
    /// <paramref name="fence"/>, so an attempt that lost its job stops at the next node.
    /// </summary>
    internal async Task CancelTreeAsync(long rootJobId, JobControlInput input, ExecutionFence fence, CancellationToken ct)
    {
        var root = await CancelAsync(rootJobId, input, fence, ct);
        var promoted = root.Action == ControlAction.Applied && root.LanePromoted;
        await JobControl.WakeWorkersIfAsync(wakeup, await WalkAsync(rootJobId, input, fence, ct) || promoted, ct);
    }

    /// <summary>
    /// Cancels the non-terminal descendant subtree top-down, one transaction per node: a crash mid-walk leaves
    /// repairable stragglers, and terminal children are recursed through too, so a re-run reaches live
    /// descendants behind them. In-subtree latches are skipped, since each node's parent is already terminal.
    /// Wakes every worker namespace once if a cancel promoted a lane member.
    /// </summary>
    internal async Task CancelDescendantsAsync(long rootJobId, JobControlInput input, CancellationToken ct) =>
        await JobControl.WakeWorkersIfAsync(wakeup, await WalkAsync(rootJobId, input, fence: null, ct), ct);

    private async Task<bool> WalkAsync(long parentJobId, JobControlInput input, ExecutionFence? fence, CancellationToken ct)
    {
        var promoted = false;
        foreach (var childId in (await execution.GetChildJobIdsAsync(parentJobId, ct)).ChildJobIds)
        {
            var row = await CancelAsync(childId, input, fence, ct);
            promoted |= row.Action == ControlAction.Applied && row.LanePromoted;
            promoted |= await WalkAsync(childId, input, fence, ct);
        }

        return promoted;
    }
}
