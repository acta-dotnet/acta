using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Acta;

/// <summary>
/// A set of rows as the JSON text a provider's set-based routine takes: one array of objects, each written by the
/// caller, with a nullable number or text written as a JSON null rather than left out, so every row has every column.
/// </summary>
internal static class JsonRows
{
    public static string Array<T>(IReadOnlyList<T> items, Action<Utf8JsonWriter, T> writeItem)
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

    public static void Number(Utf8JsonWriter writer, string name, long? value)
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

    public static void Text(Utf8JsonWriter writer, string name, string? value)
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
