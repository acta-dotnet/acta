using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>The retimed schedules as JSON text for the providers' set-based apply; each provider writes an instant its own way.</summary>
internal static class PublishCalendarJson
{
    public static string Rows(IReadOnlyList<PublishCalendarValues> rows, Action<Utf8JsonWriter, string, DateTime?> writeInstant)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var r in rows)
            {
                writer.WriteStartObject();
                writer.WriteNumber("schedule_id", r.ScheduleId);
                writer.WriteNumber("expected_version", r.ExpectedVersion);
                writer.WriteNumber("expected_state_version", r.ExpectedStateVersion);
                writeInstant(writer, "next_occurrence_at_utc", r.NextOccurrenceAtUtc);
                writer.WriteString("retimed_details", r.RetimedDetails);
                if (r.SkippedDetails is { } skipped)
                {
                    writer.WriteString("skipped_details", skipped);
                }
                else
                {
                    writer.WriteNull("skipped_details");
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
