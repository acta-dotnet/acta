using Acta.Features.Alerts;
using Acta.Features.Definitions;
using Acta.Features.Outbox;
using Acta.Features.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Acta;

/// <summary>
/// Default <see cref="IWorkerBuilder"/> implementation. Collects the worker's <see cref="ModuleRegistration"/>s
/// and identity for <c>JobsBuilder.Run</c> to fold into a <c>WorkerRegistration</c>; never instantiated by
/// consumer code.
/// </summary>
internal sealed class WorkerBuilder(IServiceCollection services) : IWorkerBuilder
{
    private readonly List<ModuleRegistration> _modules = [];
    private readonly List<AlertChannelDeclaration> _alertChannels = [];

    public string? OwnerTeam { get; set; }

    public string? Description { get; set; }

    internal IReadOnlyList<ModuleRegistration> Modules => _modules;

    internal IReadOnlyList<AlertChannelDeclaration> AlertChannels => _alertChannels;

    internal OutboxRelayRegistration? Relay { get; private set; }

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

    public IWorkerBuilder AddOutboxRelay(string sourceName, Action<IOutboxSourceBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (Relay is not null)
        {
            throw new InvalidOperationException(
                "A worker namespace registers at most one outbox relay source in v1. Call AddOutboxRelay once per worker."
            );
        }

        // Source name is an operator-stable kebab identifier, like job/schedule/channel names.
        sourceName = IdentifierSyntax.CanonicalizeKebab(sourceName, nameof(sourceName));

        // The provider extension (source.UseXxx) sets the builder's single store factory, not a shared
        // container registration: each namespace's relay keeps a distinct factory instead of one
        // winner-takes-all singleton. A supported multi-Run host attaches one source per namespace, so
        // the factory is carried on the per-namespace registration.
        var source = new OutboxSourceBuilder(sourceName);
        configure(source);

        ValidateOverride(source.Schema, "schema");
        ValidateOverride(source.Table, "table");

        if (source.QuarantineThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source.QuarantineThreshold),
                source.QuarantineThreshold,
                $"Outbox relay source '{sourceName}' sets a quarantine threshold of {source.QuarantineThreshold}. "
                    + "The threshold is the failure count at which a row quarantines and must be at least 1."
            );
        }

        var factory =
            source.StoreFactory
            ?? throw new InvalidOperationException(
                $"Outbox relay source '{sourceName}' selects no provider. Call exactly one of "
                    + "source.UsePostgres/UseSqlServer/UseSqlite."
            );

        Relay = new OutboxRelayRegistration(sourceName, source.Schema, source.Table, source.QuarantineThreshold, factory);

        // The relay's target ingestion path (owned batch enqueue). Shared across namespaces; the source
        // store and service are per-namespace and resolved by OutboxRelayRegistry from the registration.
        services.TryAddSingleton<IOutboxTarget, JobsOutboxTarget>();
        return this;
    }

    // Acta-owned names are lowercase (repo convention): a mixed-case override survives quoted DDL but the
    // relay interpolates it unquoted, so PostgreSQL folds it to lowercase and shape validation then fails
    // confusingly. The same OutboxIdentifier guard backs the provider staging extensions and DDL API.
    private static void ValidateOverride(string? value, string kind)
    {
        if (value is not null)
        {
            OutboxIdentifier.Validate(value, kind);
        }
    }
}
