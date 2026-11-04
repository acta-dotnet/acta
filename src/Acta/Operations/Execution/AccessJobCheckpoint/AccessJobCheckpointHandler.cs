namespace Acta;

/// <summary><see cref="ExecutionOperations.AccessJobCheckpointAsync"/>: one routine call per slot action, answering exactly one row.</summary>
internal sealed class AccessJobCheckpointHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<AccessJobCheckpointResponse> HandleAsync(AccessJobCheckpointCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("AccessJobCheckpoint"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.SlotAction, (short)command.Action));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointKindCode, command.Kind));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.CheckpointName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.ValueFormatId, command.ValueFormatId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Checkpoint.Value, command.Value));
                },
                DbProjectionResolver.Resolve<AccessJobCheckpointResponse>(),
                ct
            )
        );
}
