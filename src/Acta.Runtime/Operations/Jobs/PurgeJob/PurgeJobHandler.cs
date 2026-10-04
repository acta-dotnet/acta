namespace Acta.Runtime;

/// <summary><see cref="IJobs.PurgeAsync"/>: hard-deletes a terminal job; the surviving events carry its public ref.</summary>
internal sealed class PurgeJobHandler(JobLookupResolution lookup, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PurgeJobResponse> HandleAsync(PurgeJobCommand command, CancellationToken ct)
    {
        if (await lookup.ResolveAsync(command.Job, ct) is not { } jobId)
        {
            return new PurgeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(null, command.ActorKey);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("PurgeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input, includeReasonMessage: false);
            },
            DbProjectionResolver.Resolve<JobControlActionRow>(),
            ct
        );
        return new PurgeJobResponse(jobId, row.Action, row.Status, null);
    }
}
