using System.Text.Json;
using Acta.Features.Alerts;
using Xunit;

namespace Acta.Tests.Alerts;

/// <summary>
/// Pure (no-network) coverage for <see cref="SlackAlertFormatter"/>: severity drives the emoji / color,
/// the header carries the title, and the optional fields (job, occurrences, runbook) are included only
/// when present. The payload round-trips through the origin-generated JSON context.
/// </summary>
public sealed class SlackAlertFormatterTests
{
    private static AlertNotification Notification(
        AlertSeverityCode severity = AlertSeverityCode.Error,
        long? jobId = 42,
        int occurrenceCount = 1,
        string? runbookUrl = null
    ) =>
        new(
            AlertId: 1,
            JobNamespace: "orders",
            JobId: jobId,
            Severity: severity,
            Kind: AlertKindCode.FinalFailure,
            Title: "Job 'charge' failed",
            Message: "Terminal failure: boom.",
            RunbookUrl: runbookUrl,
            OccurrenceCount: occurrenceCount,
            CreatedAtUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        );

    [Theory]
    [InlineData(AlertSeverityCode.Critical, "#b00020")]
    [InlineData(AlertSeverityCode.Error, "#d32f2f")]
    [InlineData(AlertSeverityCode.Warning, "#f9a825")]
    [InlineData(AlertSeverityCode.Info, "#2962ff")]
    public void Severity_drives_attachment_color(AlertSeverityCode severity, string expectedColor)
    {
        var msg = SlackAlertFormatter.Build(Notification(severity: severity));

        var attachment = Assert.Single(msg.Attachments);
        Assert.Equal(expectedColor, attachment.Color);
    }

    [Fact]
    public void Header_carries_title_and_message()
    {
        var msg = SlackAlertFormatter.Build(Notification());

        Assert.Contains("Job 'charge' failed", msg.Text);
        Assert.Contains("Terminal failure: boom.", msg.Text);
    }

    [Fact]
    public void Core_fields_always_present()
    {
        var msg = SlackAlertFormatter.Build(Notification());
        var fields = Assert.Single(msg.Attachments).Fields;

        Assert.Contains(fields, f => f.Title == "Kind" && f.Value == nameof(AlertKindCode.FinalFailure));
        Assert.Contains(fields, f => f.Title == "Namespace" && f.Value == "orders");
        Assert.Contains(fields, f => f.Title == "Job" && f.Value == "42");
    }

    [Fact]
    public void Optional_fields_appear_only_when_present()
    {
        var bare = SlackAlertFormatter.Build(Notification(jobId: null, occurrenceCount: 1));
        var bareFields = Assert.Single(bare.Attachments).Fields;
        Assert.DoesNotContain(bareFields, f => f.Title == "Job");
        Assert.DoesNotContain(bareFields, f => f.Title == "Occurrences");
        Assert.DoesNotContain(bareFields, f => f.Title == "Runbook");

        var rich = SlackAlertFormatter.Build(Notification(occurrenceCount: 5, runbookUrl: "https://rb"));
        var richFields = Assert.Single(rich.Attachments).Fields;
        Assert.Contains(richFields, f => f.Title == "Occurrences" && f.Value == "5");
        Assert.Contains(richFields, f => f.Title == "Runbook" && f.Value == "https://rb");
    }

    [Fact]
    public void Payload_serializes_through_the_source_gen_context()
    {
        var msg = SlackAlertFormatter.Build(Notification(runbookUrl: "https://rb"));

        var json = JsonSerializer.Serialize(msg, AlertSlackJsonContext.Default.SlackMessage);

        Assert.Contains("\"text\":", json);
        Assert.Contains("\"attachments\":", json);
        Assert.Contains("\"color\":", json);
        Assert.Contains("\"fields\":", json);
        // WhenWritingNull is configured, but the model carries no nulls here; sanity-check it parses back.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("attachments").ValueKind);
    }
}
