namespace Acta;

/// <summary><see cref="IExecutionOperations.RecordNoteAsync"/>: one routine call; an unknown job id throws.</summary>
internal sealed class RecordNoteHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordNoteCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordNote"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ExecutionNumber, command.ExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ReasonMessage, command.Message));
                // Format id 0 with a NULL body is the "no detail" encoding ck_events_detail_pair expects.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DetailFormatId, command.Detail?.Format.Id ?? (byte)0));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Detail, command.Detail?.Data.ToArray()));
            },
            static reader =>
                reader.GetInt32(0) == 1 ? true : throw new InvalidOperationException("ACTA:NOTE_UNKNOWN_JOB:record_note: unknown job id"),
            ct
        );
}
