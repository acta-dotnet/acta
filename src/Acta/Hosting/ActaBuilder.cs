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
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));
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
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));

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

    public IActaBuilder DisableCli()
    {
        CliDisabled = true;
        return this;
    }
}
