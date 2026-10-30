using System.Text.Json;

namespace Acta;

/// <summary>
/// The plan as JSON text for the providers' set-based reads: one array of groups, one of rows. Each provider writes
/// an instant its own way (PostgreSQL and SQL Server parse text, SQLite stores epoch milliseconds), so the writer is
/// the caller's.
/// </summary>
internal static class AdmitFiringsJson
{
    public static string Groups(AdmitFiringsCommand command) =>
        JsonRows.Array(
            command.Groups,
            static (writer, g) =>
            {
                writer.WriteNumber("ordinal", g.Ordinal);
                JsonRows.Number(writer, "job_id", g.JobId);
                JsonRows.Number(writer, "expected_job_version", g.ExpectedJobVersion);
                JsonRows.Number(writer, "schedule_id", g.ScheduleId);
                JsonRows.Number(writer, "expected_state_version", g.ExpectedStateVersion);
                writer.WriteBoolean("admit", g.Admit);
                JsonRows.Text(writer, "envelope", g.Envelope);
                JsonRows.Text(writer, "job_ref", g.JobRef?.ToString());
                JsonRows.Text(writer, "deduplication_key", g.DeduplicationKey);
                JsonRows.Text(writer, "trigger_message", g.TriggerMessage);
                JsonRows.Text(writer, "trigger_details", g.TriggerDetails);
            }
        );

    public static string Rows(AdmitFiringsCommand command, Action<Utf8JsonWriter, string, DateTime?> writeInstant) =>
        JsonRows.Array(
            command.Rows,
            (writer, r) =>
            {
                writer.WriteNumber("group_ordinal", r.GroupOrdinal);
                writer.WriteNumber("schedule_id", r.ScheduleId);
                writer.WriteString("schedule_name", r.ScheduleName);
                writer.WriteNumber("expected_version", r.ExpectedVersion);
                writeInstant(writer, "next_occurrence_at_utc", r.NextOccurrenceAtUtc);
                writeInstant(writer, "next_evaluation_at_utc", r.NextEvaluationAtUtc);
                writer.WriteNumber("evaluation_status_code", (byte)r.EvaluationStatus);
                writer.WriteBoolean("pause_ended", r.PauseEnded);
                JsonRows.Text(writer, "admitted_details", r.AdmittedDetails);
                JsonRows.Text(writer, "skipped_details", r.SkippedDetails);
                JsonRows.Text(writer, "expired_details", r.ExpiredDetails);
                writer.WriteBoolean("expire_paused", r.ExpirePaused);
            }
        );
}
