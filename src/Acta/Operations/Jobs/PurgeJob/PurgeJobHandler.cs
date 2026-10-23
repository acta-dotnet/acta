using System.Data.Common;

namespace Acta;

/// <summary>
/// <see cref="IJobs.PurgeAsync"/>: hard-deletes a terminal job with its events and leaves one <c>job.purged</c> tombstone
/// whose details snapshot the job's ref, correlation key, and operator, since no event of the job survives to carry them.
/// A job the alert projector still owes an event is refused with <see cref="JobPurgeBlockedException"/>.
/// </summary>
internal sealed class PurgeJobHandler(
    GetJobIdHandler getJobId,
    GetJobHandler getJob,
    GetAlertFloorHandler getAlertFloor,
    IDbSession session,
    ISqlDialect dialect
)
{
    public async ValueTask<PurgeJobResponse> HandleAsync(PurgeJobCommand command, CancellationToken ct)
    {
        if (
            await getJobId.HandleAsync(new GetJobIdQuery(command.Job), ct) is not { JobId: var jobId }
            || await getJob.ReadAsync(jobId, ct) is not { } job
        )
        {
            return new PurgeJobResponse(0, ControlAction.NotFound, null, null);
        }

        // The ref and correlation key never change, so the snapshot read before the purge is the one it deletes.
        var details = EventDetails.Write(
            ("jobRef", job.JobRef.ToString()),
            ("correlationKey", job.CorrelationKey),
            ("actor", JobControlActor.Operator(command.Actor))
        );
        var floor = await getAlertFloor.HandleAsync(new GetAlertFloorQuery(JobId: jobId), ct);
        var (floorUtc, floorEventId) = floor.Floor(DateTime.MaxValue);
        try
        {
            var row = await session.ExecuteOneAsync(
                new StoreCommand("PurgeJob"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                    cmd.AddEventFields(dialect, null, details);
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertFloorUtc, floorUtc));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AlertFloorEventId, floorEventId));
                },
                DbProjectionResolver.Resolve<PurgeJobRow>(),
                ct
            );
            return new PurgeJobResponse(jobId, row.Action, row.Status, null);
        }
        catch (DbException ex) when (ex.Message.Contains("ACTA:PURGE_BLOCKED:", StringComparison.Ordinal))
        {
            throw new JobPurgeBlockedException(
                $"Job {job.JobRef} has an alert-relevant event the sys.alerts projector has not processed yet; purge it after the projector's next pass.",
                ex
            );
        }
    }
}
