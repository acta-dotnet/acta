namespace Acta;

/// <summary>
/// Optional settings for a channel declared via <c>IWorkerBuilder.AddAlertChannel</c>. Defaults route
/// every severity to an <see cref="AlertChannelStatusCode.Active"/> channel.
/// </summary>
public sealed class AlertChannelOptions
{
    /// <summary>
    /// Delivery-side severity floor: alerts below this severity are not delivered to the channel.
    /// Default <see cref="AlertSeverityCode.Info"/> delivers everything.
    /// </summary>
    public AlertSeverityCode MinSeverity { get; set; } = AlertSeverityCode.Info;

    /// <summary>
    /// Operational state the channel is registered in. Default <see cref="AlertChannelStatusCode.Active"/>.
    /// </summary>
    public AlertChannelStatusCode Status { get; set; } = AlertChannelStatusCode.Active;
}
