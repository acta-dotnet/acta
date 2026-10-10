namespace Acta;

/// <summary><see cref="JobsOperations.ResetJobStateAsync"/>: one round trip.</summary>
internal sealed class ResetJobStateHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ResetJobStateCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ResetJobState"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, command.JobId)),
            ct
        );
}
