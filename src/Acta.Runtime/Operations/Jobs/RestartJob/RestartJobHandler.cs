namespace Acta.Runtime;

/// <summary><see cref="IJobs.RestartAsync"/>: reruns a job that is not executing, at its slot's next run when it is recurring.</summary>
internal sealed class RestartJobHandler(
    JobLookupResolution lookup,
    GetLiveSchedulesHandler liveSchedules,
    IActaClock clock,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    public async ValueTask<RestartJobResponse> HandleAsync(RestartJobCommand command, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new RestartJobResponse(0, ControlAction.NotFound, null, null);
        }

        var (reject, nextRun) = await JobControl.RecurringNextRunAsync(liveSchedules, clock, jobId, ct);
        if (reject)
        {
            return new RestartJobResponse(jobId, ControlAction.Rejected, JobStatusCode.Paused, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.ActorKey, command.ExpectedVersion);
        var row = await RestartAsync(jobId, input, nextRun, ct);
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new RestartJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>restart_job</c> call by id at <paramref name="nextRunAtUtc"/>, or now when null: no lookup, no wake.</summary>
    internal Task<JobControlRow> RestartAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("RestartJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobRuntime.NextRunAtUtc, nextRunAtUtc));
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<JobControlRow>(),
            ct
        );
}
