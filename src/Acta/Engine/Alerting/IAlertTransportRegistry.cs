namespace Acta;

/// <summary>
/// Resolves an <see cref="IAlertTransport"/> by its <see cref="IAlertTransport.TransportKind"/>. Populated
/// from the registered transports at DI configuration time.
/// </summary>
internal interface IAlertTransportRegistry
{
    /// <summary>The transport for <paramref name="transportKind"/>, or <c>null</c> when none is registered.</summary>
    IAlertTransport? Resolve(string transportKind);
}
