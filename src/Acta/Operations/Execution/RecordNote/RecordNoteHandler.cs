namespace Acta;

/// <summary><see cref="ExecutionOperations.RecordNoteAsync"/>: one routine call; an unknown job id throws.</summary>
internal sealed class RecordNoteHandler(IDbSession session, ISqlDialect dialect)
{
    public Task HandleAsync(RecordNoteCommand command, CancellationToken ct) =>
        session.ExecuteAsync(
            new StoreCommand("RecordNote"),
            cmd =>
            {
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Job.JobId, command.JobId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.ExecutionNumber, command.ExecutionNumber));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.WorkerId, command.WorkerId));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.EventMessage, command.Message));
                // Format id 0 with a NULL body is the "no details" encoding ck_events_details_pair expects.
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.DetailsFormatId, command.Detail?.Format.Id ?? (byte)0));
                cmd.Parameters.Add(dialect.CreateParameter(ActaSchema.Event.Details, command.Detail?.Data.ToArray()));
            },
            static reader =>
                reader.GetInt32(0) == 1 ? true : throw new InvalidOperationException("ACTA:NOTE_UNKNOWN_JOB:record_note: unknown job id"),
            ct
        );
}
