namespace Acta;

/// <summary>
/// Outcome of one delivery attempt by an <see cref="IAlertTransport"/>.
/// </summary>
public enum AlertDeliveryOutcome : byte
{
    /// <summary>Delivered successfully; the alert is marked <c>Delivered</c>.</summary>
    Delivered = 1,

    /// <summary>Transient failure (timeout, 5xx, rate-limited); the alert retries after a backoff.</summary>
    Retryable = 2,

    /// <summary>Permanent failure (bad endpoint, 4xx, malformed); the alert is marked terminal <c>Failed</c>.</summary>
    Permanent = 3,
}

/// <summary>
/// The fields a transport renders, projected from a <c>alerts</c> row by the delivery loop.
/// <see cref="RunbookUrl"/> comes from the job's definition, resolved at delivery time.
/// </summary>
public sealed record AlertNotification(
    long AlertId,
    string JobNamespace,
    long? JobId,
    AlertSeverityCode Severity,
    AlertKindCode Kind,
    string Title,
    string Message,
    string? RunbookUrl,
    int OccurrenceCount,
    DateTime CreatedAtUtc
);

/// <summary>
/// The resolved channel a notification is delivered to: its transport kind, endpoint, and opaque
/// per-transport config bytes (decode with <see cref="ConfigFormatId"/> via the payload serializer registry).
/// </summary>
public sealed record AlertTarget(
    string ChannelName,
    string TransportKind,
    string Endpoint,
    byte ConfigFormatId,
    ReadOnlyMemory<byte> Config
);

/// <summary>
/// Swappable delivery transport for one <see cref="TransportKind"/> (e.g. <c>"slack-webhook"</c>). Register
/// implementations in DI; the <c>sys.alerts</c> delivery loop resolves the one matching a configured
/// channel's transport kind.
/// </summary>
public interface IAlertTransport
{
    /// <summary>The transport kind this transport handles (kebab-case, matches startup channel configuration).</summary>
    string TransportKind { get; }

    /// <summary>
    /// Deliver <paramref name="notification"/> to <paramref name="target"/>. Never throws for an expected
    /// transport failure; returns <see cref="AlertDeliveryOutcome.Retryable"/> or
    /// <see cref="AlertDeliveryOutcome.Permanent"/> instead.
    /// </summary>
    Task<AlertDeliveryOutcome> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct);
}

/// <summary>
/// Resolves an <see cref="IAlertTransport"/> by its <see cref="IAlertTransport.TransportKind"/>. Populated
/// from the registered transports at DI configuration time.
/// </summary>
public interface IAlertTransportRegistry
{
    /// <summary>The transport for <paramref name="transportKind"/>, or <c>null</c> when none is registered.</summary>
    IAlertTransport? Resolve(string transportKind);
}
