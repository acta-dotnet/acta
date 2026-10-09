namespace Acta;

/// <summary><see cref="Execution.StartExecutionAsync"/>: one routine call answers the start action.</summary>
internal sealed class StartExecutionHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<StartExecutionResponse> HandleAsync(StartExecutionCommand command, CancellationToken ct)
    {
        var rows = await session.ExecuteAsync(
            new StoreCommand("StartExecution"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Runtime.ExecutionNumber, command.ExpectedExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeaseTtlSeconds, command.LeaseTtlSeconds));
            },
            reader => reader.IsDBNull(0) ? (byte?)null : reader.GetByteFromNumeric(0),
            ct
        );

        return rows.Count > 0 && rows[^1] is { } action
            ? new StartExecutionResponse((StartExecutionAction)action)
            : throw new InvalidOperationException("StartExecution returned no action.");
    }
}
