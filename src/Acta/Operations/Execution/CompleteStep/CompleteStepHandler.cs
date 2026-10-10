namespace Acta;

/// <summary><see cref="ExecutionOperations.CompleteStepAsync"/>: one routine call records the attempt and answers its decision.</summary>
internal sealed class CompleteStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CompleteStepResponse> HandleAsync(CompleteStepCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("CompleteStep"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepSucceeded, command.Succeeded));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.ResultFormatId, command.ResultFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Result, command.Result));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.ReasonCode, command.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.ReasonMessage, command.ReasonMessage));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryDelaySeconds, command.DelaySeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepMaxAttempts, command.MaxAttempts));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryWindowSeconds, command.RetryWindowSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Version, command.ExpectedVersion));
            },
            DbProjectionResolver.Resolve<CompleteStepResponse>(),
            ct
        );
}
