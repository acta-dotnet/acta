using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One alert row in a <see cref="IAlerts.ListAsync"/> page. Channel configuration,
/// endpoints, and deduplication keys are delivery internals and are never exposed by list reads. JSON
/// serialization carries the public job ref and hides the numeric job id; the ref goes null once
/// the subject job row is purged (alerts outlive their job).
/// </summary>
/// <param name="AlertRef">This alert's public ref.</param> <param name="AlertId">Internal alert row id.</param> <param name="JobNamespace">Owning namespace name.</param> <param name="JobId">Subject job id, or null.</param>
/// <param name="JobRef">Subject job's public ref, or null when job-less or purged.</param>
/// <param name="Origin">What raised the alert.</param> <param name="Severity">Alert severity.</param> <param name="Kind">Alert kind.</param>
/// <param name="Title">Alert title.</param> <param name="Message">Alert message.</param>
/// <param name="ChannelName">Logical delivery channel name.</param> <param name="OccurrenceCount">Deduplicated occurrence count.</param>
/// <param name="ResolvedAtUtc">When the alert resolved, or null while open.</param> <param name="DeliveryStatus">Delivery pipeline status.</param>
/// <param name="RetryCount">Delivery retry attempts so far.</param> <param name="RetryAfterUtc">Earliest next delivery attempt, or null.</param>
/// <param name="CreatedAtUtc">Row insert instant.</param> <param name="ModifiedAtUtc">Last row change instant.</param>
/// <param name="AcknowledgedAtUtc">When the alert was acknowledged, or null while open.</param>
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
    byte RetryCount,
    DateTime? RetryAfterUtc,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc,
    DateTime? AcknowledgedAtUtc
);
