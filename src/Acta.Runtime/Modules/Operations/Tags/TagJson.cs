using System.Buffers;
using System.Text;
using System.Text.Json;
using Acta.Runtime.Kernel;

namespace Acta.Runtime.Modules.Operations.Tags;

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
                writer.WriteString("name", tag.Name);
                if (tag.Value is null)
                {
                    writer.WriteNull("value");
                    writer.WriteNull("value_search");
                }
                else
                {
                    writer.WriteString("value", tag.Value);
                    writer.WriteString("value_search", TagValueSearch.Normalize(tag.Value));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
