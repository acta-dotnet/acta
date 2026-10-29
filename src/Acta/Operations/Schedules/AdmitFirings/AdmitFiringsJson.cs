using System.Buffers;
using System.Text;
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
        Array(
            command.Groups,
            static (writer, g) =>
            {
                writer.WriteNumber("ordinal", g.Ordinal);
                Number(writer, "job_id", g.JobId);
                Number(writer, "expected_job_version", g.ExpectedJobVersion);
                Number(writer, "schedule_id", g.ScheduleId);
                Number(writer, "expected_state_version", g.ExpectedStateVersion);
                writer.WriteBoolean("admit", g.Admit);
                Text(writer, "envelope", g.Envelope);
                Text(writer, "job_ref", g.JobRef?.ToString());
                Text(writer, "deduplication_key", g.DeduplicationKey);
                Text(writer, "trigger_message", g.TriggerMessage);
                Text(writer, "trigger_details", g.TriggerDetails);
            }
        );

    public static string Rows(AdmitFiringsCommand command, Action<Utf8JsonWriter, string, DateTime?> writeInstant) =>
        Array(
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
                Text(writer, "admitted_details", r.AdmittedDetails);
                Text(writer, "skipped_details", r.SkippedDetails);
                Text(writer, "expired_details", r.ExpiredDetails);
                writer.WriteBoolean("expire_paused", r.ExpirePaused);
            }
        );

    private static void Number(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string Array<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T> writeItem)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writeItem(writer, item);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
