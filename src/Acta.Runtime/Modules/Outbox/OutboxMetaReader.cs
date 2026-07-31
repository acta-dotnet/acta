using System.Text.Json;
using System.Text.Json.Serialization;

namespace Acta.Runtime.Modules.Outbox;

/// <summary>
/// AOT-safe source-generated parse of the external-outbox <c>meta</c> bag, the mirror of
/// <c>OutboxMetaWriter</c>. Reads the documented <c>{"tags":[{"name":..,"value":..}]}</c> shape from the
/// single shared <see cref="OutboxMetaDto"/>: a root object, an optional ordered <c>tags</c> array where
/// <c>name</c> is a required string and <c>value</c> is a string or JSON null. A malformed shape is a
/// deterministic contract error the relay quarantines.
/// </summary>
internal static class OutboxMetaReader
{
    /// <summary>
    /// The tags declared in <paramref name="metaJson"/>, or null when there is no <c>meta</c>. Throws
    /// <see cref="OutboxContractException"/> when the JSON is present but does not match the shape.
    /// </summary>
    public static IReadOnlyList<TagInput>? Parse(string? metaJson)
    {
        if (string.IsNullOrWhiteSpace(metaJson))
        {
            return null;
        }

        OutboxMetaDto? meta;
        try
        {
            meta = JsonSerializer.Deserialize(metaJson, OutboxMetaJsonContext.Default.OutboxMetaDto);
        }
        catch (JsonException ex)
        {
            throw new OutboxContractException($"meta is not valid JSON: {ex.Message}");
        }

        if (meta?.Tags is not { Count: > 0 } tags)
        {
            return null;
        }

        var result = new List<TagInput>(tags.Count);
        foreach (var tag in tags)
        {
            if (string.IsNullOrEmpty(tag.Name))
            {
                throw new OutboxContractException("meta.tags entry is missing a non-empty 'name'.");
            }
            result.Add(new TagInput(tag.Name, tag.Value));
        }
        return result;
    }
}

/// <summary>
/// The single shared wire shape of the external-outbox <c>meta</c> column: a root object with an optional
/// ordered <c>tags</c> array of <c>{"name":..,"value":..}</c> entries. Written by the producer-side
/// <c>OutboxMetaWriter</c> (Acta.Relational) and parsed by the relay-side <see cref="OutboxMetaReader"/>
/// (Acta), so the two sides cannot drift. A presence-only tag carries an explicit JSON <c>null</c> value;
/// <see cref="JsonIgnoreCondition.Never"/> keeps that null in the serialized form.
/// </summary>
internal sealed record OutboxMetaDto(List<OutboxTagDto>? Tags);

/// <summary>One <c>meta.tags</c> entry: a required <c>name</c> and a string-or-null <c>value</c>.</summary>
internal sealed record OutboxTagDto(string? Name, string? Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(OutboxMetaDto))]
internal sealed partial class OutboxMetaJsonContext : JsonSerializerContext;
