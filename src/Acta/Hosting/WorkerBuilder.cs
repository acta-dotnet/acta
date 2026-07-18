using Acta.Features.Alerts;
using Acta.Features.Definitions;
using Acta.Features.Workers;

namespace Acta;

/// <summary>
/// Default <see cref="IWorkerBuilder"/> implementation. Collects the worker's <see cref="ModuleRegistration"/>s
/// and identity for <c>JobsBuilder.Run</c> to fold into a <c>WorkerRegistration</c>; never instantiated by
/// consumer code.
/// </summary>
internal sealed class WorkerBuilder : IWorkerBuilder
{
    private readonly List<ModuleRegistration> _modules = [];
    private readonly List<AlertChannelDeclaration> _alertChannels = [];

    public string? OwnerTeam { get; set; }

    public string? Description { get; set; }

    internal IReadOnlyList<ModuleRegistration> Modules => _modules;

    internal IReadOnlyList<AlertChannelDeclaration> AlertChannels => _alertChannels;

    public IWorkerBuilder AddModule<TManifest>()
        where TManifest : class, IActaManifest
    {
        var type = typeof(TManifest);
        if (!_modules.Any(m => m.ManifestType == type))
        {
            _modules.Add(new ModuleRegistration(type, static () => TManifest.Descriptors));
        }
        return this;
    }

    public IWorkerBuilder AddAlertChannel(string name, string transportKind, string endpoint, Action<AlertChannelOptions>? configure = null)
    {
        // Channel name and transport kind are operator-stable kebab identifiers (like job, schedule, and
        // format names); kebab rejects the underscore-prefixed framework reserved shape. Endpoint is a
        // free-form transport target (URL, ARN, address), so only non-empty is required.
        name = IdentifierSyntax.CanonicalizeKebab(name, nameof(name));
        IdentifierSyntax.ValidateKebab(transportKind, nameof(transportKind));
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var options = new AlertChannelOptions();
        configure?.Invoke(options);

        // Last declaration of a name wins (the builder snapshots; no throw on a re-declared channel).
        _alertChannels.RemoveAll(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        _alertChannels.Add(new AlertChannelDeclaration(name, transportKind, endpoint, options.Status, options.MinSeverity));
        return this;
    }
}
