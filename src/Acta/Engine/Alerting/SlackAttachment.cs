using System.Text.Json.Serialization;

namespace Acta;

/// <summary>A colored Slack attachment carrying labelled fields.</summary>
internal sealed record SlackAttachment(
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("fields")] IReadOnlyList<SlackField> Fields
);
