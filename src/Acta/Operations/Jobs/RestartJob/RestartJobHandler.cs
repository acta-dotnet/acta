namespace Acta;

/// <summary><see cref="IJobs.RestartAsync"/>: reruns a job that is not executing; a recurring job's slot with no firing outstanding returns to Idle instead.</summary>
internal sealed class RestartJobHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect, WorkerWakeupPublisher wakeup)
{
    public async ValueTask<RestartJobResponse> HandleAsync(RestartJobCommand command, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new RestartJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion);
        var row = await RestartAsync(jobId, input, null, ct);
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new RestartJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>restart_job</c> call by id at <paramref name="nextRunAtUtc"/>, or now when null: no lookup, no wake.</summary>
    internal Task<RestartJobRow> RestartAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("RestartJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                JobControl.BindEvent(cmd, dialect, input);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.NextRunAtUtc, nextRunAtUtc));
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<RestartJobRow>(),
            ct
        );
}
