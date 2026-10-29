namespace Acta;

/// <summary>
/// <see cref="IJobs.RescheduleAsync"/>: moves a waiting job's next run, re-arming a paused or suspended one. An Idle
/// permanent slot, or a Paused one with no firing outstanding, takes a manual firing at the instant instead, with the
/// envelope a schedule trigger writes; the routine uses it only then.
/// </summary>
internal sealed class RescheduleJobHandler(
    GetJobIdHandler getJobId,
    IDbSession session,
    ISqlDialect dialect,
    WorkerWakeupPublisher wakeup,
    IActaClock clock
)
{
    public async ValueTask<RescheduleJobResponse> HandleAsync(RescheduleJobCommand command, CancellationToken ct)
    {
        if (command.NextRunAtUtc is { } instant)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(instant, DateTime.MinValue, "nextRunAtUtc");
        }

        if (await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId })
        {
            return new RescheduleJobResponse(0, ControlAction.NotFound, null, null);
        }

        var input = JobControl.Operator(command.ReasonMessage, command.Actor, command.ExpectedVersion);
        var envelope = new FiringEnvelope(Guid.CreateVersion7(), FiringEnvelope.ManualKind, await clock.GetUtcNowAsync(ct), []).ToJson();
        var row = await session.ExecuteOneAsync(
            new StoreCommand("RescheduleJob"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.NextRunAtUtc, command.NextRunAtUtc));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.FiringEnvelope, envelope));
                JobControl.BindEvent(cmd, dialect, input);
                JobControl.BindExpectedVersion(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<RescheduleJobRow>(),
            ct
        );
        await JobControl.WakeIfReadyAsync(wakeup, row.Action, row.Status, ct);
        return new RescheduleJobResponse(jobId, row.Action, row.Status, row.Version);
    }
}
