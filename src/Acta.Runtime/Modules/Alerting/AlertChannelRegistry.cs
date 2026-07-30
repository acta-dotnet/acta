using Acta.Modules.Alerting.Api;

namespace Acta.Modules.Alerting;

internal sealed class AlertChannelRegistry : IAlertChannelRegistry
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
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));
        channelName = IdentifierSyntax.CanonicalizeKebab(channelName, nameof(channelName), IdentifierSyntax.ExtendedMaxLength);
        return _channels.TryGetValue((namespaceName, channelName), out var channel) ? channel : null;
    }

    public bool IsConfigured(string namespaceName, string channelName) => Resolve(namespaceName, channelName) is not null;

    public IReadOnlyCollection<string> NamesForNamespace(string namespaceName)
    {
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));
        return _channels
            .Keys.Where(k => string.Equals(k.Namespace, namespaceName, StringComparison.Ordinal))
            .Select(k => k.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

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
