namespace Acta;

/// <summary><see cref="ExecutionOperations.StartStepAsync"/>: one routine call answers the step's decision.</summary>
internal sealed class StartStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartStepResponse> HandleAsync(StartStepCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("StartStep"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AtMostOnce, command.AtMostOnce));
            },
            DbProjectionResolver.Resolve<StartStepResponse>(),
            ct
        );
}
