using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Alerting;

/// <summary>
/// One alert-relevant <c>events</c> row projected for the <c>sys.alerts</c> generate phase, joined to
/// its definition's alert policy. The projector classifies the reason in C# from the
/// <c>(ExecutionStatus, ToStatus, ReasonCode)</c> triple, never from the mutable <c>runtimes.failure_count</c>.
/// </summary>
internal sealed record AlertableEvent(
    long EventId,
    long? JobId,
    int DefinitionId,
    string JobName,
    JobAlertProfileCode AlertProfile,
    string? AlertChannelName,
    ExecutionStatusCode? ExecutionStatus,
    JobStatusCode? ToStatus,
    JobEventReasonCode? ReasonCode,
    string? ReasonMessage
);

/// <summary>
/// One <c>alerts</c> row due for delivery. Carries notification content and the logical channel name;
/// transport resolution happens against the worker's in-memory channel registry.
/// </summary>
internal sealed record DeliverableAlert(
    long AlertId,
    long? JobId,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string? RunbookUrl,
    int OccurrenceCount,
    DateTime CreatedAtUtc,
    byte RetryCount,
    string ChannelName
);

/// <summary>
/// Result of an alert control transition (acknowledge / resolve): the action plus the alert's
/// <c>acknowledged_at_utc</c> / <c>resolved_at_utc</c> after the attempt. Shared by both verbs, which
/// each return one <c>(action, acknowledged_at_utc, resolved_at_utc)</c> row.
/// </summary>
internal sealed record AlertControlOutcome(JobControlActionInternal Action, DateTime? AcknowledgedAtUtc, DateTime? ResolvedAtUtc);

/// <summary>
/// Flat alert list row in SELECT order; dedupe and channel-config columns are never selected.
/// </summary>
internal sealed record JobAlertListProjectionRow(
    long JobAlertId,
    string JobNamespace,
    long? JobId,
    AlertOriginCode Origin,
    AlertSeverityCode Severity,
    AlertKindCode Reason,
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
    Guid? JobRef,
    DateTime? AcknowledgedAtUtc
)
{
    public JobAlertListItem ToItem() =>
        new(
            JobAlertId,
            JobNamespace,
            JobId,
            JobRef is { } jobRef ? new JobRef(jobRef) : null,
            Origin,
            Severity,
            Reason,
            Title,
            Message,
            ChannelName,
            OccurrenceCount,
            ResolvedAtUtc,
            DeliveryStatus,
            RetryCount,
            RetryAfterUtc,
            CreatedAtUtc,
            ModifiedAtUtc,
            AcknowledgedAtUtc
        );
}
