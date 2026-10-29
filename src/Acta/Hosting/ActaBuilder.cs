using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Acta;

/// <summary>
/// Default <see cref="IActaBuilder"/> implementation. Constructed by
/// <see cref="ActaServiceCollectionExtensions.UseActa"/>; never instantiated directly by
/// consumer code.
/// </summary>
internal sealed class ActaBuilder(IServiceCollection services) : IActaBuilder
{
    private readonly List<WorkerRegistration> _workers = [];
    private readonly List<CatalogDeclaration> _references = [];
    private readonly List<Func<IServiceProvider, IJobPipelineBehavior>> _pipelineBehaviors = [];
    private readonly HashSet<Type> _pipelineBehaviorTypes = [];

    public IServiceCollection Services { get; } = services;

    /// <summary>
    /// Workers declared via <c>Run</c>, in declaration order. Read by
    /// <see cref="ActaServiceCollectionExtensions.UseActa"/> after the configure callback to
    /// fan out one <c>WorkerRuntime</c> per worker.
    /// </summary>
    internal IReadOnlyList<WorkerRegistration> Workers => _workers;

    /// <summary>
    /// Catalogs visible to typed enqueue: every <c>Reference</c> plus every <c>Run</c> worker's
    /// namespace and manifests. Read by
    /// <see cref="ActaServiceCollectionExtensions.UseActa"/> to build the JobTypeRegistry.
    /// </summary>
    internal IEnumerable<CatalogDeclaration> Catalogs =>
        _references.Concat(_workers.Select(w => new CatalogDeclaration(w.NamespaceName, w.Manifests)));

    /// <summary>
    /// Pipeline-behavior resolvers in registration order (outermost first). Read by
    /// <see cref="ActaServiceCollectionExtensions.UseActa"/> to construct the per-worker
    /// <c>JobBehaviorPipeline</c>.
    /// </summary>
    internal IReadOnlyList<Func<IServiceProvider, IJobPipelineBehavior>> PipelineBehaviors => _pipelineBehaviors;

    /// <summary>The calendars registered in code, read by <see cref="ActaServiceCollectionExtensions.UseActa"/> as the process registry.</summary>
    internal CalendarRegistry Calendars { get; } = new();

    /// <summary>Suppresses the CLI host swap.</summary>
    internal bool CliDisabled { get; private set; }

    public IActaBuilder ConfigureOptions(Action<JobsOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    public IActaBuilder Run<TManifest>(string namespaceName, string? ownerTeam = null, string? description = null)
        where TManifest : class, IJobManifest =>
        Run(
            namespaceName,
            w =>
            {
                w.OwnerTeam = ownerTeam;
                w.Description = description;
                w.AddManifest<TManifest>();
            }
        );

    public IActaBuilder Run(string namespaceName, Action<IWorkerBuilder> configure)
    {
        namespaceName = NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkerBuilder();
        configure(builder);

        if (builder.OwnerTeam is not null && string.IsNullOrWhiteSpace(builder.OwnerTeam))
        {
            throw new ArgumentException("ownerTeam, when supplied, must be non-whitespace.", nameof(configure));
        }
        if (builder.Description is { Length: > 256 })
        {
            throw new ArgumentException($"Description must be <= 256 characters ({builder.Description.Length} given).", nameof(configure));
        }
        if (_workers.Any(w => string.Equals(w.NamespaceName, namespaceName, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"A worker for namespace '{namespaceName}' is already registered. Each worker in a process must own a distinct namespace."
            );
        }

        _workers.Add(
            new WorkerRegistration(
                namespaceName,
                builder.OwnerTeam,
                builder.Description,
                builder.Manifests,
                builder.AlertChannels,
                builder.Relay
            )
        );
        return this;
    }

    public IActaBuilder Reference<TManifest>(string namespaceName)
        where TManifest : class, IJobManifest
    {
        namespaceName = NameSyntax.Canonicalize(namespaceName, nameof(namespaceName), reserveSystem: true);

        if (
            !_references.Any(r =>
                string.Equals(r.NamespaceName, namespaceName, StringComparison.Ordinal) && r.Manifests[0].ManifestType == typeof(TManifest)
            )
        )
        {
            _references.Add(
                new CatalogDeclaration(namespaceName, [new ManifestRegistration(typeof(TManifest), static () => TManifest.Descriptors)])
            );
        }
        return this;
    }

    public IActaBuilder AddPayloadSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSerializer>()
        where TSerializer : class, IJobPayloadSerializer
    {
        Services.AddSingleton<IJobPayloadSerializer, TSerializer>();
        return this;
    }

    public IActaBuilder UseJsonPayloads(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        // Registered after UseActa's built-in json serializer; the registry is last-wins per format id.
        Services.AddSingleton<IJobPayloadSerializer>(JsonJobPayloadSerializer.WithResolver(resolver));
        return this;
    }

    public IActaBuilder AddPipelineBehavior<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped
    )
        where TBehavior : class, IJobPipelineBehavior
    {
        if (_pipelineBehaviorTypes.Add(typeof(TBehavior)))
        {
            Services.Add(new ServiceDescriptor(typeof(TBehavior), typeof(TBehavior), lifetime));
            _pipelineBehaviors.Add(static sp => sp.GetRequiredService<TBehavior>());
        }
        return this;
    }

    public IActaBuilder AddCalendar(string name, IActaCalendar implementation)
    {
        Calendars.Add(name, implementation);
        return this;
    }

    public IActaBuilder AddAvailabilityCalendar(string name, IActaAvailabilityCalendar implementation)
    {
        Calendars.AddAvailability(name, implementation);
        return this;
    }

    public IActaBuilder DisableCli()
    {
        CliDisabled = true;
        return this;
    }
}

/// <summary>
/// Default <see cref="IWorkerBuilder"/> implementation. Collects the worker's <see cref="ManifestRegistration"/>s
/// and identity for <c>ActaBuilder.Run</c> to fold into a <c>WorkerRegistration</c>; never instantiated by
/// consumer code.
/// </summary>
internal sealed class WorkerBuilder : IWorkerBuilder
{
    private readonly List<ManifestRegistration> _manifests = [];
    private readonly List<AlertChannelDeclaration> _alertChannels = [];

    public string? OwnerTeam { get; set; }

    public string? Description { get; set; }

    internal IReadOnlyList<ManifestRegistration> Manifests => _manifests;

    internal IReadOnlyList<AlertChannelDeclaration> AlertChannels => _alertChannels;

    internal OutboxRelayRegistration? Relay { get; private set; }

    public IWorkerBuilder AddManifest<TManifest>()
        where TManifest : class, IJobManifest
    {
        var type = typeof(TManifest);
        if (!_manifests.Any(m => m.ManifestType == type))
        {
            _manifests.Add(new ManifestRegistration(type, static () => TManifest.Descriptors));
        }
        return this;
    }

    public IWorkerBuilder AddAlertChannel(string name, string transportKind, string endpoint, Action<AlertChannelOptions>? configure = null)
    {
        // Channel name and transport kind are operator-stable kebab identifiers (like job, schedule, and
        // format names); kebab rejects the underscore-prefixed framework reserved shape. Endpoint is a
        // free-form transport target (URL, ARN, address), so only non-empty is required.
        name = NameSyntax.Canonicalize(name, nameof(name));
        transportKind = NameSyntax.Canonicalize(transportKind, nameof(transportKind));
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
        sourceName = NameSyntax.Canonicalize(sourceName, nameof(sourceName));

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
                nameof(configure),
                source.QuarantineThreshold,
                $"Outbox relay source '{sourceName}' sets a QuarantineThreshold of {source.QuarantineThreshold}. "
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
        return this;
    }

    /// <summary>
    /// Acta-owned names are lowercase: a mixed-case override survives quoted DDL but the relay
    /// interpolates it unquoted, so PostgreSQL folds it to lowercase and shape validation then
    /// fails confusingly. The same <see cref="OutboxIdentifier"/> guard backs the provider staging
    /// extensions and DDL API.
    /// </summary>
    private static void ValidateOverride(string? value, string kind)
    {
        if (value is not null)
        {
            OutboxIdentifier.Validate(value, kind);
        }
    }
}
