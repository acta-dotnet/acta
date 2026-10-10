namespace Acta;

/// <summary><see cref="IJobs.PurgeAsync"/>: hard-deletes a terminal job; the surviving events carry its public ref.</summary>
internal sealed class PurgeJobHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect)
{
    public async ValueTask<PurgeJobResponse> HandleAsync(PurgeJobCommand command, CancellationToken ct)
    {
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new PurgeJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(null, command.Actor);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("PurgeJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                JobControl.BindActor(cmd, dialect, input, includeReasonMessage: false);
            },
            DbProjectionResolver.Resolve<PurgeJobRow>(),
            ct
        );
        return new PurgeJobResponse(jobId, row.Action, row.Status, null);
    }
}
