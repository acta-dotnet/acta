namespace Acta;

/// <summary><see cref="SignalsOperations.WaitJobSignalAsync"/>: one routine call answering the wait decision.</summary>
internal sealed class WaitJobSignalHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<WaitJobSignalResponse> HandleAsync(WaitJobSignalCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("WaitJobSignal"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointKindCode, command.Kind));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WaitTimeoutSeconds, command.TimeoutSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.WaitDueAtUtc, command.DueAtUtc));
                },
                DbProjectionResolver.Resolve<WaitJobSignalResponse>(),
                ct
            )
        );
}
