using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// The single-alert read model returned by <see cref="IAlerts.GetAsync"/>. Today it carries the
/// list row's fields; it is a distinct type because a detail read can grow additively (delivery
/// history, the alerting event's detail) without the list row paying for it.
///
/// <para>Two delivery fields read differently than their names suggest. <c>RetryCount</c> is the
/// attempts spent in the current send series, not over the row's life: a delivered send ends the
/// series and resets it. <c>RetryAfterUtc</c> is the earliest instant the alert may be sent again -
/// the next retry while delivery is unsettled, the next reminder once it has settled - and null when
/// nothing is scheduled.</para>
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
