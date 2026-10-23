namespace Acta;

/// <summary><see cref="ExecutionOperations.CompleteStepAsync"/>: one routine call records the attempt and answers its decision.</summary>
internal sealed class CompleteStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CompleteStepResponse> HandleAsync(CompleteStepCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("CompleteStep"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.StepName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepSucceeded, command.Succeeded));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.ResultFormatId, command.ResultFormatId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Result, command.Result));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.FailureCode, command.FailureCode));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.FailureMessage, command.FailureMessage));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryDelaySeconds, command.DelaySeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepMaxAttempts, command.MaxAttempts));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepRetryWindowSeconds, command.RetryWindowSeconds));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Version, command.ExpectedVersion));
                },
                DbProjectionResolver.Resolve<CompleteStepResponse>(),
                ct
            )
        );
}
