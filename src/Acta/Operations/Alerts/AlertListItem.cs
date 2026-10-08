using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One alert row in a <see cref="IAlerts.ListAsync"/> page. Channel configuration, endpoints, and deduplication keys
/// are delivery internals, never exposed by list reads. JSON carries the public job ref and hides the numeric job id;
/// the ref goes null once the subject job row is purged, since alerts outlive their job. <c>RetryCount</c> is the
/// attempts spent in the current send series (a delivered send ends it), and <c>RetryAfterUtc</c> the earliest instant
/// the alert may be sent again, the next retry while delivery is unsettled or the next reminder once settled.
/// </summary>
public sealed record AlertListItem(
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
    int RetryCount,
    DateTime? RetryAfterUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? AcknowledgedAtUtc
);
