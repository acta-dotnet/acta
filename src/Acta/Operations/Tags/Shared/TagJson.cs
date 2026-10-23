using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Acta;

internal static class TagJson
{
    public static string Write(IReadOnlyList<TagInput> tags)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var tag in tags)
            {
                writer.WriteStartObject();
                writer.WriteString("tag_name", tag.Name);
                if (tag.Value is null)
                {
                    writer.WriteNull("tag_value");
                    writer.WriteNull("tag_value_search");
                }
                else
                {
                    writer.WriteString("tag_value", tag.Value);
                    writer.WriteString("tag_value_search", TagValueSearch.Normalize(tag.Value));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
