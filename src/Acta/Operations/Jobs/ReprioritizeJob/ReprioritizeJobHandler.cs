namespace Acta;

/// <summary><see cref="IJobs.ReprioritizeAsync"/>: changes a non-terminal job's claim priority.</summary>
internal sealed class ReprioritizeJobHandler(
    GetJobIdHandler getJobId,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    public async ValueTask<ReprioritizeJobResponse> HandleAsync(ReprioritizeJobCommand command, CancellationToken ct)
    {
        RequireBand(command.Priority);

        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new ReprioritizeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("ReprioritizeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.JobPriorityCode, command.Priority));
                JobControl.BindEvent(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<ReprioritizeJobRow>(),
            ct
        );
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new ReprioritizeJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    // No claim reads a priority that is not a band, so the job would be stored and never run.
    private static void RequireBand(JobPriorityCode priority)
    {
        if (priority == JobPriorityCode.Unspecified || !Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "Priority is not a priority band.");
        }
    }
}
