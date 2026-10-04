namespace Acta.Runtime;

/// <summary><see cref="Execution.CompleteStepAsync"/>: one routine call records the attempt and answers its decision.</summary>
internal sealed class CompleteStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CompleteStepDecision> HandleAsync(CompleteStepCommand command, CancellationToken ct) =>
        session.ExecuteOneAsync(
            new StoreCommand("CompleteStep"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Name, command.Name));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepSucceeded, command.Succeeded));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ResultFormatId, command.ResultFormatId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Result, command.Result));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ReasonCode, command.ReasonCode));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.ReasonMessage, command.ReasonMessage));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryDelaySeconds, command.DelaySeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepMaxAttempts, command.MaxAttempts));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryWindowSeconds, command.RetryWindowSeconds));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobStep.Version, command.ExpectedVersion));
            },
            DbProjectionResolver.Resolve<CompleteStepDecision>(),
            ct
        );
}
