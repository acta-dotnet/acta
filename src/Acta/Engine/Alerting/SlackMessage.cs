using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Top-level Slack webhook payload.</summary>
internal sealed record SlackMessage(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("attachments")] IReadOnlyList<SlackAttachment> Attachments
);
