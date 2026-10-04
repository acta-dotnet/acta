namespace Acta.Runtime;

/// <summary><see cref="IJobs.ResumeAsync"/>: moves a paused job to Ready, at its slot's next run when it is recurring.</summary>
internal sealed class ResumeJobHandler(
    JobLookupResolution lookup,
    GetLiveSchedulesHandler liveSchedules,
    IActaClock clock,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup
)
{
    public async ValueTask<ResumeJobResponse> HandleAsync(ResumeJobCommand command, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new ResumeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var (reject, nextRun) = await JobControl.RecurringNextRunAsync(liveSchedules, clock, jobId, ct);
        if (reject)
        {
            return new ResumeJobResponse(jobId, ControlAction.Rejected, JobStatusCode.Paused, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.ActorKey, command.ExpectedVersion);
        var row = await ResumeAsync(jobId, input, nextRun, ct);
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new ResumeJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>resume_job</c> call by id at <paramref name="nextRunAtUtc"/>, or now when null: no lookup, no wake.</summary>
    internal Task<JobControlRow> ResumeAsync(long jobId, JobControlInput input, DateTime? nextRunAtUtc, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("ResumeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input);
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Runtime.NextRunAtUtc, nextRunAtUtc));
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<JobControlRow>(),
            ct
        );
}
