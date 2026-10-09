using System.Buffers;
using System.Text.Json;

namespace Acta;

/// <summary>
/// Reads and writes the system JSON envelope stored on the parent's <c>sys.child.{id}</c> signal
/// slot. Hand-rolled rather than the registered serializer: the contract is pinned and must not
/// change when a consumer swaps the JSON format. The envelope carries terminal state only
/// (<c>childJobId</c>, <c>status</c>); the failure reason lives on the child's event timeline.
/// <c>complete_execution</c> builds the same shape in SQL for its in-transaction raise;
/// <see cref="Write"/> serves the C#-side raises (cancel, reclaim, maintenance backstop).
/// </summary>
internal static class ChildOutcomeEnvelope
{
    public static byte[] Write(long childJobId, JobStatusCode status)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("childJobId", childJobId);
            writer.WriteNumber("status", (short)status);
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static ChildJobOutcome Parse(ReadOnlySpan<byte> utf8)
    {
        long childJobId = 0;
        short status = 0;

        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            if (reader.ValueTextEquals("childJobId"u8))
            {
                reader.Read();
                childJobId = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("status"u8))
            {
                reader.Read();
                status = reader.GetInt16();
            }
            else
            {
                reader.Skip();
            }
        }

        return childJobId == 0 || status == 0
            ? throw new InvalidOperationException("Child outcome envelope is missing childJobId or status.")
            : new ChildJobOutcome(childJobId, (JobStatusCode)status);
    }
}
