using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single-alert read model returned by <see cref="IAlerts.GetAsync"/>. Today it carries the
/// list row's fields; it is a distinct type because a detail read can grow additively (delivery
/// history, the alerting event's detail) without the list row paying for it.
/// </summary>
public sealed record AlertDetail(
    AlertRef AlertRef,
    [property: JsonIgnore] long AlertId,
    string JobNamespace,
    [property: JsonIgnore] long? JobId,
    JobRef? JobRef,
    AlertOriginCode Origin,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string ChannelName,
    int OccurrenceCount,
    DateTime? ResolvedAtUtc,
    AlertDeliveryStatusCode DeliveryStatus,
    byte RetryCount,
    DateTime? RetryAfterUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? AcknowledgedAtUtc
);
