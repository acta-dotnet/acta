namespace Acta;

/// <summary>
/// One <c>alerts</c> row due for delivery: a first attempt, a due retry, or a due reminder for an incident still open.
/// Carries the notification content and the logical channel name; the transport resolves against the worker's
/// in-memory channel registry. <see cref="Version"/> is the row's version at selection, so a settlement that raced a
/// resolve or another settlement writes nothing. <see cref="Origin"/> decides whether settling this send schedules
/// the next reminder: an automatic alert tracks a condition Acta watches and re-notifies while it stays open; a manual
/// one belongs to the handler that raised it.
/// </summary>
[DbProjection(ByName = true)]
internal sealed record DeliverableAlert(
    long AlertId,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string? RunbookUrl,
    int OccurrenceCount,
    DateTime CreatedAtUtc,
    int RetryCount,
    string ChannelName,
    Guid AlertRef,
    Guid? JobRef,
    int Version,
    AlertOriginCode Origin
);
