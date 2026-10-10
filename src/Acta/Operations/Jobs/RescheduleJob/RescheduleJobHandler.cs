namespace Acta;

/// <summary><see cref="IJobs.RescheduleAsync"/>: moves a waiting job's next run, re-arming a paused or suspended one.</summary>
internal sealed class RescheduleJobHandler(GetJobIdHandler getJobId, IDbSession session, ISqlDialect dialect, WorkerWakeupPublisher wakeup)
{
    public async ValueTask<RescheduleJobResponse> HandleAsync(RescheduleJobCommand command, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(command.NextRunAtUtc, DateTime.MinValue, "nextRunAtUtc");
        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new RescheduleJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.ActorKey, command.ExpectedVersion);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("RescheduleJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Runtime.NextRunAtUtc, command.NextRunAtUtc));
                JobControl.BindActor(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<RescheduleJobRow>(),
            ct
        );
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new RescheduleJobResponse(jobId, row.Action, row.Status, row.Version);
    }
}
