namespace Acta;

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
    /// transport failure; returns <see cref="AlertSendStatus.Retryable"/> or
    /// <see cref="AlertSendStatus.Permanent"/> instead. A send has ten seconds: <paramref name="ct"/> is cancelled then,
    /// and a send still running is given up as retryable, so honor the token.
    /// </summary>
    Task<AlertSendStatus> SendAsync(AlertNotification notification, AlertTarget target, CancellationToken ct);
}
