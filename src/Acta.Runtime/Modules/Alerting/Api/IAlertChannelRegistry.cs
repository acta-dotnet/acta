using Acta.Modules.Alerting.Api;

namespace Acta.Modules.Alerting.Api;

/// <summary>
/// Alerting's declared channel API: worker startup resolves and validates declared channels through
/// this, never through alerting internals. <see cref="ForWorkers"/> is the construction entry point
/// for the direct-constructor test seam; composition registers the DI singleton.
/// </summary>
internal interface IAlertChannelRegistry
{
    AlertChannelDeclaration? Resolve(string namespaceName, string channelName);

    bool IsConfigured(string namespaceName, string channelName);

    IReadOnlyCollection<string> NamesForNamespace(string namespaceName);

    static IAlertChannelRegistry ForWorkers(IEnumerable<WorkerRegistration> workers) => new AlertChannelRegistry(workers);
}
