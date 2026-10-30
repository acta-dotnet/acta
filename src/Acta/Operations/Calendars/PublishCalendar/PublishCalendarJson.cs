using System.Text.Json;

namespace Acta;

/// <summary>The retimed schedules as JSON text for the providers' set-based apply; each provider writes an instant its own way.</summary>
internal static class PublishCalendarJson
{
    public static string Rows(IReadOnlyList<PublishCalendarValues> rows, Action<Utf8JsonWriter, string, DateTime?> writeInstant) =>
        JsonRows.Array(
            rows,
            (writer, r) =>
            {
                writer.WriteNumber("schedule_id", r.ScheduleId);
                writer.WriteNumber("expected_version", r.ExpectedVersion);
                writer.WriteNumber("expected_state_version", r.ExpectedStateVersion);
                writeInstant(writer, "next_occurrence_at_utc", r.NextOccurrenceAtUtc);
                writer.WriteString("retimed_details", r.RetimedDetails);
                JsonRows.Text(writer, "skipped_details", r.SkippedDetails);
            }
        );
}
