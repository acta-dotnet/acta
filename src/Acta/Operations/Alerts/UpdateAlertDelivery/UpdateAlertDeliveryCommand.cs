namespace Acta;

/// <summary>One delivery attempt's outcome for an alert, guarded by the version delivery selected it at.</summary>
internal sealed record UpdateAlertDeliveryCommand(
    long AlertId,
    int ExpectedVersion,
    AlertDeliveryStatusCode Status,
    int RetryCount,
    DateTime? RetryAfterUtc
);
