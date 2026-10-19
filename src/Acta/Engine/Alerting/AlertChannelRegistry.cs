namespace Acta;

/// <summary>
/// Alerting's declared channels by worker namespace: worker startup validates routing against them and
/// delivery resolves each alert's channel through them, never through alerting internals. Every worker
/// namespace carries an implicit <c>default</c> log channel unless it declares its own.
/// </summary>
internal sealed class AlertChannelRegistry
{
    private readonly Dictionary<(string Namespace, string Name), AlertChannelDeclaration> _channels = [];

    public AlertChannelRegistry(IEnumerable<WorkerRegistration> workers)
    {
        ArgumentNullException.ThrowIfNull(workers);

        foreach (var worker in workers)
        {
            RegisterDefault(worker.NamespaceName);

            foreach (var channel in worker.AlertChannels)
            {
                _channels[(worker.NamespaceName, channel.Name)] = channel;
            }
        }
    }

    public AlertChannelDeclaration? Resolve(string namespaceName, string channelName)
    {
        namespaceName = NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true);
        channelName = NameSyntax.Canonicalize(channelName, nameof(channelName), maxLength: NameSyntax.ExtendedMaxLength);
        return _channels.TryGetValue((namespaceName, channelName), out var channel) ? channel : null;
    }

    public bool IsConfigured(string namespaceName, string channelName) => Resolve(namespaceName, channelName) is not null;

    private void RegisterDefault(string namespaceName)
    {
        _channels[(namespaceName, "default")] = new AlertChannelDeclaration(
            "default",
            AlertTransportKinds.Log,
            Endpoint: "default",
            AlertChannelStatusCode.Active,
            AlertSeverityCode.Info
        );
    }
}
