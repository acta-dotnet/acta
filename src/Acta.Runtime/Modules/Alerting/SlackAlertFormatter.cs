using System.Globalization;
using System.Text.Json.Serialization;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// Builds the Slack incoming-webhook payload for an alert: a header line with a severity emoji and title,
/// the message body, and a colored attachment carrying reason, namespace, job, occurrences, and runbook.
/// Pure (no I/O); the JSON shape is origin-generated for NativeAOT via <see cref="AlertSlackJsonContext"/>.
/// </summary>
internal static class SlackAlertFormatter
{
    public static SlackMessage Build(AlertNotification n)
    {
        var (emoji, color) = n.Severity switch
        {
            AlertSeverityCode.Critical => ("\U0001F6A8", "#b00020"),
            AlertSeverityCode.Error => ("❌", "#d32f2f"),
            AlertSeverityCode.Warning => ("⚠️", "#f9a825"),
            _ => ("ℹ️", "#2962ff"),
        };

        var fields = new List<SlackField> { new("Kind", n.Kind.ToString(), true), new("Namespace", n.JobNamespace, true) };
        if (n.JobRef is { } jobRef)
        {
            fields.Add(new("Job", jobRef.ToString(), true));
        }
        if (n.OccurrenceCount > 1)
        {
            fields.Add(new("Occurrences", n.OccurrenceCount.ToString(CultureInfo.InvariantCulture), true));
        }
        if (!string.IsNullOrEmpty(n.RunbookUrl))
        {
            fields.Add(new("Runbook", n.RunbookUrl, false));
        }

        var text = $"{emoji} *{n.Title}*\n{n.Message}";
        return new SlackMessage(text, [new SlackAttachment(color, fields)]);
    }
}

/// <summary>Top-level Slack webhook payload.</summary>
internal sealed record SlackMessage(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("attachments")] IReadOnlyList<SlackAttachment> Attachments
);

/// <summary>A colored Slack attachment carrying labelled fields.</summary>
internal sealed record SlackAttachment(
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("fields")] IReadOnlyList<SlackField> Fields
);

/// <summary>One Slack attachment field; <c>Short</c> renders two-per-row.</summary>
internal sealed record SlackField(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("short")] bool Short
);

/// <summary>
/// Origin-generated JSON metadata for the Slack payload; keeps Slack delivery reflection-free under
/// NativeAOT.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SlackMessage))]
internal sealed partial class AlertSlackJsonContext : JsonSerializerContext;
