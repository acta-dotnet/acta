namespace Acta;

/// <summary>
/// What one <see cref="IAlertTransport.SendAsync"/> call reports about its delivery attempt.
/// </summary>
public enum AlertSendStatus : byte
{
    /// <summary>Delivered successfully; the alert is marked <c>Delivered</c>.</summary>
    Delivered = 1,

    /// <summary>Transient failure (timeout, 5xx, rate-limited); the alert retries after a backoff.</summary>
    Retryable = 2,

    /// <summary>Permanent failure (bad endpoint, 4xx, malformed); the alert is marked terminal <c>Failed</c>.</summary>
    Permanent = 3,
}
