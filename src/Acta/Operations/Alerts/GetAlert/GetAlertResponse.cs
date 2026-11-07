using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// One alert. retryCount counts the attempts of the current send series, which a delivered send ends, and retryAfterUtc is the earliest
/// time the alert may be sent again.
/// </summary>
/// <param name="AlertId">The alert's internal id; the JSON omits it and carries alertRef.</param>
/// <param name="JobId">The internal id of the job the alert is about; the JSON omits it and carries jobRef.</param>
/// <param name="AlertRef">The alert's ref, alr_ followed by 32 hex digits.</param>
/// <param name="JobNamespace">The namespace the alert belongs to.</param>
/// <param name="JobRef">The ref of the job the alert is about, when it is about a job.</param>
/// <param name="Origin">How the alert came to be raised.</param>
/// <param name="Severity">The alert's severity.</param>
/// <param name="Kind">What raised the alert.</param>
/// <param name="Title">The alert's short title.</param>
/// <param name="Message">The alert's message.</param>
/// <param name="ChannelName">The name of the alert channel the alert is delivered through.</param>
/// <param name="OccurrenceCount">How many times the incident has been raised while this alert was open.</param>
/// <param name="ResolvedAtUtc">When the alert was resolved, in UTC; null while it is open.</param>
/// <param name="DeliveryStatus">Where delivery of the alert to its channel stands.</param>
/// <param name="RetryCount">How many delivery attempts have failed so far.</param>
/// <param name="RetryAfterUtc">When delivery is tried again, in UTC; null when no retry is pending.</param>
/// <param name="CreatedAtUtc">When the row was created, in UTC.</param>
/// <param name="ModifiedAtUtc">When the row last changed, in UTC.</param>
/// <param name="AcknowledgedAtUtc">When the alert was acknowledged, in UTC; null while it has not been.</param>
public sealed record GetAlertResponse(
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
