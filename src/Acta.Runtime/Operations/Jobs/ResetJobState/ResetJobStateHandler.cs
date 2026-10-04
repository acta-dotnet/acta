namespace Acta.Runtime;

/// <summary><c>ctx.ResetStateAsync</c>: clears a job's durable step and checkpoint state ahead of a fresh attempt.</summary>
internal sealed class ResetJobStateHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(ResetJobStateCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("ResetJobState"),
            cmd => cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.Id, command.JobId)),
            ct
        );
}
