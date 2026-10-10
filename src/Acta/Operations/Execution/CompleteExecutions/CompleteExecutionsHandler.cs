namespace Acta;

/// <summary><see cref="Execution.CompleteExecutionsAsync"/>: one routine call writes the whole batch.</summary>
internal sealed class CompleteExecutionsHandler(IDbSession session, ISqlDialect dialect)
{
    public async Task<CompleteExecutionsResponse> HandleAsync(CompleteExecutionsCommand command, CancellationToken ct) =>
        new(
            await session.ExecuteAsync(
                new StoreCommand("CompleteExecutions"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.WorkerId));
                    dialect.BindCompleteExecutions(cmd, command);
                },
                DbProjectionResolver.Resolve<CompleteExecutionsRow>(),
                ct
            )
        );
}
