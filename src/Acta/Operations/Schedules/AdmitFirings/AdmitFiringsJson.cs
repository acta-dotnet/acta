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
                writer.WriteNumber("job_id", g.JobId);
                writer.WriteNumber("expected_job_version", g.ExpectedJobVersion);
                writer.WriteBoolean("admit", g.Admit);
                if (g.Envelope is null)
                {
                    writer.WriteNull("envelope");
                }
                else
                {
                    writer.WriteString("envelope", g.Envelope);
                }
            }
        );

    public static string Rows(AdmitFiringsCommand command, Action<Utf8JsonWriter, string, DateTime?> writeInstant) =>
        Array(
            command.Rows,
            (writer, r) =>
            {
                writer.WriteNumber("schedule_id", r.ScheduleId);
                writer.WriteNumber("job_id", r.JobId);
                writer.WriteString("schedule_name", r.ScheduleName);
                writer.WriteNumber("expected_version", r.ExpectedVersion);
                writeInstant(writer, "next_occurrence_at_utc", r.NextOccurrenceAtUtc);
                writeInstant(writer, "decided_through_at_utc", r.DecidedThroughAtUtc);
                writeInstant(writer, "next_evaluation_at_utc", r.NextEvaluationAtUtc);
                writer.WriteNumber("evaluation_status_code", (byte)r.EvaluationStatus);
                writer.WriteBoolean("pause_ended", r.PauseEnded);
                Text(writer, "admitted_details", r.AdmittedDetails);
                Text(writer, "skipped_details", r.SkippedDetails);
            }
        );

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
