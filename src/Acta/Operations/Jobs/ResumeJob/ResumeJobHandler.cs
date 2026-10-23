namespace Acta;

/// <summary><see cref="IJobs.ResumeAsync"/>: moves a paused job to Ready, at its slot's next run when it is recurring.</summary>
internal sealed class ResumeJobHandler(
    GetJobIdHandler getJobId,
    GetLiveSchedulesHandler liveSchedules,
    IActaClock clock,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    public async ValueTask<ResumeJobResponse> HandleAsync(ResumeJobCommand command, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new ResumeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var live = (await liveSchedules.HandleAsync(new GetLiveSchedulesQuery(jobId), ct)).Schedules;
        var (reject, nextRun) = await JobControl.RecurringNextRunAsync(live, clock, ct);
        if (reject)
        {
            return new ResumeJobResponse(jobId, ControlAction.Rejected, JobStatusCode.Paused, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion);
        var row = await ResumeAsync(jobId, input, nextRun, ct);
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new ResumeJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>resume_job</c> call by id at <paramref name="nextRunAtUtc"/>, or now when null: no lookup, no wake.</summary>
    internal Task<ResumeJobRow> ResumeAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ResumeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                JobControl.BindEvent(cmd, dialect, input);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.NextRunAtUtc, nextRunAtUtc));
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<ResumeJobRow>(),
            ct
        );
}
