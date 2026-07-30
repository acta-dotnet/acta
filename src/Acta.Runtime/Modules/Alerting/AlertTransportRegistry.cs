using Acta.Payloads;

namespace Acta.Modules.Alerting;

/// <summary>
/// Resolves an <see cref="IAlertTransport"/> by its <c>transport_kind</c>. Built from the registered
/// transports; last registration wins for a kind. Mirrors <c>JobPayloadSerializerRegistry</c>.
/// </summary>
internal sealed class AlertTransportRegistry : IAlertTransportRegistry
{
    private readonly Dictionary<string, IAlertTransport> _byKind;

    public AlertTransportRegistry(IEnumerable<IAlertTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        _byKind = new Dictionary<string, IAlertTransport>(StringComparer.Ordinal);
        foreach (var t in transports)
        {
            _byKind[t.TransportKind] = t;
        }
    }

    public IAlertTransport? Resolve(string transportKind) => _byKind.TryGetValue(transportKind, out var t) ? t : null;
}
