namespace Acta.Runtime;

/// <summary>
/// Validated alert raise; construct via <see cref="Create"/> so channel canonicalization, deduplication-key
/// normalization, and bounded-prose truncation happen once, identically for every caller.
/// </summary>
internal sealed record RaiseAlertCommand(
    string JobNamespace,
    long? JobId,
    AlertOriginCode Origin,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string ChannelName,
    AlertDeliveryStatusCode DeliveryStatus,
    string? DeduplicationKey,
    long? SourceEventId,
    Guid AlertRef
)
{
    public static RaiseAlertCommand Create(
        string jobNamespace,
        long? jobId,
        AlertOriginCode origin,
        AlertSeverityCode severity,
        AlertKindCode kind,
        string title,
        string message,
        string channelName,
        AlertDeliveryStatusCode deliveryStatus,
        string? deduplicationKey,
        long? sourceEventId
    )
    {
        channelName = IdentifierSyntax.CanonicalizeKebab(channelName, nameof(channelName), ActaTextLimits.AlertChannelName);
        if (deduplicationKey is not null)
        {
            deduplicationKey = IdentifierSyntax.NormalizeKey(deduplicationKey, nameof(deduplicationKey), ActaTextLimits.AlertDedupeKey);
        }

        return new RaiseAlertCommand(
            jobNamespace,
            jobId,
            origin,
            severity,
            kind,
            title.Truncate(ActaTextLimits.AlertTitle)!,
            message.Truncate(ActaTextLimits.AlertMessage)!,
            channelName,
            deliveryStatus,
            deduplicationKey.Truncate(ActaTextLimits.AlertDedupeKey),
            sourceEventId,
            // The candidate public ref is minted here so every caller carries one; the raise consumes it
            // only when the upsert actually inserts, so one incident keeps one ref across every repeat it
            // absorbs and the next incident on the same key gets a new one.
            Acta.AlertRef.New().Value
        );
    }

    /// <summary>
    /// A handler's own <c>JobContext.AlertAsync</c>: manual origin and kind, the <c>default</c> channel when
    /// none is named, and no projected event behind it, so it always applies and never moves the projection
    /// high-water mark. A null deduplication key always inserts; a non-null key collapses onto the one open
    /// row carrying it, and opens a fresh row once that one is resolved.
    /// </summary>
    public static RaiseAlertCommand Manual(
        string jobNamespace,
        long jobId,
        AlertSeverityCode severity,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey
    ) =>
        Create(
            jobNamespace,
            jobId,
            AlertOriginCode.Manual,
            severity,
            AlertKindCode.Manual,
            title,
            message,
            channelName ?? "default",
            AlertDeliveryStatusCode.Pending,
            deduplicationKey,
            sourceEventId: null
        );
}
