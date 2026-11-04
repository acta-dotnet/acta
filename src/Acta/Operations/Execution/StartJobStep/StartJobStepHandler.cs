namespace Acta;

/// <summary><see cref="ExecutionOperations.StartJobStepAsync"/>: one routine call answers the step's decision.</summary>
internal sealed class StartJobStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<StartJobStepResponse> HandleAsync(StartJobStepCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("StartJobStep"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.StepName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.AtMostOnce, command.AtMostOnce));
                },
                DbProjectionResolver.Resolve<StartJobStepResponse>(),
                ct
            )
        );
}
