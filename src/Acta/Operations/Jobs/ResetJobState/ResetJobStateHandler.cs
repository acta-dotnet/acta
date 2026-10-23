namespace Acta;

/// <summary>
/// <see cref="JobsOperations.ResetJobStateAsync"/>: one round trip, which clears nothing unless the attempt still holds
/// the job; a server answers the refusal as a reset that applied to no row.
/// </summary>
internal sealed class ResetJobStateHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ResetJobStateCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteAsync(
                new StoreCommand("ResetJobState"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                },
                static reader =>
                    reader.GetInt32(0) == 1
                        ? true
                        : throw new ExecutionFencedException("reset_job_state: the job is no longer executing this attempt."),
                ct
            )
        );
}
