using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// The JSON arguments the set-based outbox finalizes bind: an id array, or the per-row records
/// <see cref="OutboxSchema.Sql.RowRecords"/> describes.
/// </summary>
internal static class OutboxJson
{
    /// <summary>The physical width of <c>last_error</c> on every provider, so a long error never overflows the write.</summary>
    private const int MaxLastError = 512;

    /// <summary>
    /// The claimed ids as a JSON array of canonical GUID text. GUIDs contain no JSON-significant characters,
    /// so no escaping is required.
    /// </summary>
    public static string IdArray(IReadOnlyList<Guid> ids)
    {
        var builder = new StringBuilder(ids.Count * 40 + 2).Append('[');
        for (var index = 0; index < ids.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(ids[index].ToString()).Append('"');
        }

        return builder.Append(']').ToString();
    }

    /// <summary>NULL, not an empty array, is the every-quarantined-row form the operator SQL tests for.</summary>
    public static string? OptionalIdArray(IReadOnlyList<Guid>? ids) => ids is null ? null : IdArray(ids);

    /// <summary>The per-row reschedule records.</summary>
    public static string Reschedules(IReadOnlyList<OutboxReschedule> rows) =>
        Write(writer =>
        {
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("outbox_id", row.OutboxId.ToString());
                writer.WriteNumber("failure_count", row.FailureCount);
                writer.WriteNumber("backoff_seconds", row.BackoffSeconds);
                WriteStringOrNull(writer, "last_error", LastError(row.LastError));
                writer.WriteEndObject();
            }
        });

    /// <summary>The per-row quarantine records.</summary>
    public static string Quarantines(IReadOnlyList<OutboxQuarantine> rows) =>
        Write(writer =>
        {
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteString("outbox_id", row.OutboxId.ToString());
                writer.WriteNumber("failure_count", row.FailureCount);
                WriteStringOrNull(writer, "last_error", LastError(row.LastError));
                writer.WriteEndObject();
            }
        });

    // Utf8JsonWriter is AOT-safe and escapes last_error correctly.
    private static string Write(Action<Utf8JsonWriter> writeRows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            writeRows(writer);
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Cut through <see cref="MessageTruncator"/>, not raw: last_error carries handler exception text, and a
    /// raw cut can split a surrogate pair, which Npgsql's UTF-8 encoder rejects - failing the very write that
    /// records why the row failed.
    /// </summary>
    private static string? LastError(string? value) => MessageTruncator.Truncate(value, MaxLastError);

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
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
}
