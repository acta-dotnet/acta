namespace Acta;

/// <summary>
/// The <c>transport_kind</c> identifiers of the built-in <see cref="IAlertTransport"/> implementations, so
/// consumers wiring channels via <c>IWorkerBuilder.AddAlertChannel</c> reference a constant rather than
/// hardcoding the string. Custom transports define their own kebab-case kind.
/// </summary>
public static class AlertTransportKinds
{
    /// <summary>The always-present zero-dependency transport that writes the alert to the logger.</summary>
    public const string Log = "log";

    /// <summary>The built-in Slack incoming-webhook transport.</summary>
    public const string SlackWebhook = "slack-webhook";
}
