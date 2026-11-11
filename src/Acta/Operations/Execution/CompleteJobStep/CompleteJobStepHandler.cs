namespace Acta;

/// <summary><see cref="ExecutionOperations.CompleteJobStepAsync"/>: one routine call records the outcome and answers its decision.</summary>
internal sealed class CompleteJobStepHandler(IDbSession session, ISqlDialect dialect)
{
    public Task<CompleteJobStepResponse> HandleAsync(CompleteJobStepCommand command, CancellationToken ct) =>
        ExecutionFencedException.TranslateAsync(
            session.ExecuteOneAsync(
                new StoreCommand("CompleteJobStep"),
                cmd =>
                {
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.JobId, command.Fence.JobId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobState.ExecutionNumber, command.Fence.ExecutionNumber));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.LeasedByWorkerId, command.Fence.WorkerId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.StepName, command.Name));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Sql.StepSucceeded, command.Succeeded));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.ResultFormatId, command.ResultFormatId));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Result, command.Result));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.FailureMessage, command.FailureMessage));
                    cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Step.Version, command.ExpectedVersion));
                },
                DbProjectionResolver.Resolve<CompleteJobStepResponse>(),
                ct
            )
        );
}
