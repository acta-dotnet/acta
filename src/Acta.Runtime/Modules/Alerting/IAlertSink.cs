namespace Acta.Modules.Alerting;

/// <summary>
/// The manual-alert seam execution depends on: <c>JobContext.AlertAsync</c> raises through this
/// instead of reaching into <see cref="IAlertStore"/>, so alert persistence stays an alerting
/// concern. Returns the raised alert's occurrence count, mirroring the store.
/// </summary>
internal interface IAlertSink
{
    Task<int> RaiseAsync(RaiseJobAlertCommand command, CancellationToken ct);
}

/// <summary>Forwards manual alerts to the owned alert store.</summary>
internal sealed class AlertStoreSink(IAlertStore store) : IAlertSink
{
    public Task<int> RaiseAsync(RaiseJobAlertCommand command, CancellationToken ct) => store.RaiseJobAlertAsync(command, ct);
}
