namespace Acta.Runtime;

/// <summary><see cref="IExecution.RecordJobNoteAsync"/>: one routine call; an unknown job id throws.</summary>
internal sealed class RecordJobNoteHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordJobNoteCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordJobNote"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ExecutionNumber, command.ExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.ReasonMessage, command.Message));
                // Format id 0 with a NULL body is the "no detail" encoding ck_events_detail_pair expects.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.DetailFormatId, command.Detail?.Format.Id ?? (byte)0));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.JobEvent.Detail, command.Detail?.Data.ToArray()));
            },
            static reader =>
                reader.GetInt32(0) == 1
                    ? true
                    : throw new InvalidOperationException("ACTA:NOTE_UNKNOWN_JOB:record_job_note: unknown job id"),
            ct
        );
}
