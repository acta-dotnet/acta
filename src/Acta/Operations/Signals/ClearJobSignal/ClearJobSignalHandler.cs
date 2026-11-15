namespace Acta;

/// <summary>
/// The operator clear behind <c>IJobs.ClearSignalAsync</c>: canonicalizes the name, resolves the job, refuses a system job,
/// and deletes the signal's checkpoint in one round trip while the job is finished or a permanent job between firings.
/// </summary>
internal sealed class ClearJobSignalHandler(IDbSession session, ISqlDialect dialect, GetJobHandler getJob)
{
    public async ValueTask<ClearJobSignalResponse> HandleAsync(ClearJobSignalCommand command, CancellationToken ct)
    {
        var name = NameSyntax.Canonicalize(command.Name, "name", NameSyntax.ExtendedMaxLength, reserveSystem: true);
        if (await getJob.HandleAsync(new GetJobQuery(command.Job), ct) is not { JobId: var jobId } job)
        {
            return new ClearJobSignalResponse(0, ControlAction.NotFound, null);
        }

        // A system job's signals are commands it has yet to apply, such as the operator requeues and discards parked on
        // sys.outbox, so a clear would drop one unapplied.
        if (NameSyntax.StartsWithSystemPrefix(job.JobName))
        {
            return new ClearJobSignalResponse(jobId, ControlAction.Rejected, job.Status, "system-job");
        }

        var input = JobControl.Operator($"Signal '{name}' cleared.", command.Actor);
        var row = await session.ExecuteOneAsync(
            new StoreCommand("ClearJobSignal"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, jobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, name));
                JobControl.BindEvent(cmd, dialect, input);
            },
            DbProjectionResolver.Resolve<ClearJobSignalRow>(),
            ct
        );

        // A live job is the store's one refusal: it may be reading the signal, so only a job that is done with it forgets it.
        return new ClearJobSignalResponse(jobId, row.Action, row.Status, row.Action == ControlAction.Rejected ? "job-not-finished" : null);
    }
}
