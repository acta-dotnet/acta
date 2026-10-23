namespace Acta;

/// <summary><see cref="ExecutionOperations.StartStepAsync"/>: one routine call answers the step's decision.</summary>
internal sealed class StartStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartStepResponse> HandleAsync(StartStepCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("StartStep"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.StepName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AtMostOnce, command.AtMostOnce));
                },
                DbProjectionResolver.Resolve<StartStepResponse>(),
                ct
            )
        );
}
