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
    public static readonly JobControlInput ParentCancelled = new(
        new JobControlActor(ActorCode.Sys),
        EventReasonCode.JobParentCancelled,
        "Ancestor job cancelled."
    );

    public async ValueTask<CancelJobResponse> HandleAsync(CancelJobCommand command, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new CancelJobResponse(0, ControlAction.NotFound, null, null);
        }

        var row = await CancelAsync(jobId, JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion), ct);
        if (row.Action == ControlAction.Applied)
        {
            var released = row.ParentId is { } parentId && await RaiseChildLatch.Run(signals, jobId, parentId, JobStatusCode.Cancelled, ct);
            await JobControl.WakeWorkersIfAsync(wakeup, released || row.LanePromoted, ct);
        }

        // An already-cancelled job walks too, so cancelling it again resumes a walk a crash cut short.
        if (row.Status == JobStatusCode.Cancelled)
        {
            await CancelDescendantsAsync(jobId, ParentCancelled, ct);
        }

        return new CancelJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>cancel_job</c> call by id that wakes the job's waiters when it applied: no lookup, no latch, no worker wake.</summary>
    internal async Task<CancelJobRow> CancelAsync(long jobId, JobControlInput input, CancellationToken ct)
    {
        var row = await session.ExecuteOneAsync(
            new StoreCommand("CancelJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<CancelJobRow>(),
            ct
        );
        if (row.Action == ControlAction.Applied)
        {
            await wakeup.WakeAsync(WorkerWakeupChannel.JobCompletion(jobId), WorkerWakeupReason.JobFinished, ct);
        }

        return row;
    }

    /// <summary><see cref="JobsOperations.CancelTreeAsync"/>: the root's cancel, then the descendant walk.</summary>
    internal async Task CancelTreeAsync(long rootJobId, JobControlInput input, CancellationToken ct)
    {
        var root = await CancelAsync(rootJobId, input, ct);
        var promoted = root.Action == ControlAction.Applied && root.LanePromoted;
        await JobControl.WakeWorkersIfAsync(wakeup, await WalkAsync(rootJobId, input, ct) || promoted, ct);
    }

    /// <summary>
    /// Cancels the non-terminal descendant subtree top-down, one transaction per node: a crash mid-walk leaves
    /// repairable stragglers, and terminal children are recursed through too, so a re-run reaches live
    /// descendants behind them. In-subtree latches are skipped, since each node's parent is already terminal.
    /// Wakes every worker namespace once if a cancel promoted a lane member.
    /// </summary>
    internal async Task CancelDescendantsAsync(long rootJobId, JobControlInput input, CancellationToken ct) =>
        await JobControl.WakeWorkersIfAsync(wakeup, await WalkAsync(rootJobId, input, ct), ct);

    private async Task<bool> WalkAsync(long parentJobId, JobControlInput input, CancellationToken ct)
    {
        var promoted = false;
        foreach (var childId in (await execution.GetChildJobIdsAsync(parentJobId, ct)).ChildJobIds)
        {
            var row = await CancelAsync(childId, input, ct);
            promoted |= row.Action == ControlAction.Applied && row.LanePromoted;
            promoted |= await WalkAsync(childId, input, ct);
        }

        return promoted;
    }
}
