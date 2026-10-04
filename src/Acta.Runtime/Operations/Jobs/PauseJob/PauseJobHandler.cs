namespace Acta.Runtime;

/// <summary><see cref="IJobs.PauseAsync"/>: pauses a claimable job, waking the claim loops when its lane promoted the next member.</summary>
internal sealed class PauseJobHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect, WorkerWakeupPublisher wakeup)
{
    public async ValueTask<PauseJobResponse> HandleAsync(PauseJobCommand command, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new PauseJobResponse(0, ControlAction.NotFound, null, null);
        }

        var row = await PauseAsync(jobId, JobControl.Operator(command.ReasonMessage, command.ActorKey, command.ExpectedVersion), ct);
        await JobControl.WakeWorkersIfAsync(wakeup, row.LanePromoted, ct);
        return new PauseJobResponse(jobId, row.Action, row.Status, row.Version);
    }

    /// <summary>One <c>pause_job</c> call by id: no lookup, no wake.</summary>
    internal Task<PauseJobRow> PauseAsync(long jobId, JobControlInput input, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("PauseJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<PauseJobRow>(),
            ct
        );
}
