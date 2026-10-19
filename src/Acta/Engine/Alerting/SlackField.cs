using System.Text.Json.Serialization;

namespace Acta;

/// <summary>One Slack attachment field; <c>Short</c> renders two-per-row.</summary>
internal sealed record SlackField(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("short")] bool Short
);
