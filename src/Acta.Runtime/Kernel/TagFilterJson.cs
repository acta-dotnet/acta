using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Acta.Kernel;

internal static class TagFilterJson
{
    public static string? Normalize(IReadOnlyList<TagFilter>? filters, string queryName)
    {
        if (filters is null || filters.Count == 0)
        {
            return null;
        }

        if (filters.Count > TagLimits.MaxFiltersPerQuery)
        {
            throw new ArgumentOutOfRangeException(
                nameof(filters),
                filters.Count,
                $"{queryName} tag filters are limited to {TagLimits.MaxFiltersPerQuery} entries."
            );
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < filters.Count; i++)
            {
                var filter = filters[i] ?? throw new ArgumentException("Tag filter entries must not be null.", nameof(filters));
                var name = IdentifierSyntax.CanonicalizeUserDottedKebab(
                    filter.Name,
                    $"Tags[{i}].{nameof(TagFilter.Name)}",
                    TagLimits.MaxNameLength
                );
                if (!seen.Add(name))
                {
                    throw new ArgumentException($"{queryName} contains duplicate tag filter name '{name}'.", nameof(filters));
                }

                if (filter.Value is { } value)
                {
                    IdentifierSyntax.ValidateDisplayValue(value, $"Tags[{i}].{nameof(TagFilter.Value)}", TagLimits.MaxValueLength);
                }

                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("value_search", TagValueSearch.Normalize(filter.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
