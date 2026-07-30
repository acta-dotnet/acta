using System.Text.Json.Serialization.Metadata;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Workers;
using Acta.Payloads;
using Microsoft.Extensions.DependencyInjection;

namespace Acta;

/// <summary>
/// Default <see cref="IJobsBuilder"/> implementation. Constructed by
/// <see cref="ActaServiceCollectionExtensions.UseActa"/>; never instantiated directly by
/// consumer code.
/// </summary>
internal sealed class JobsBuilder(IServiceCollection services) : IJobsBuilder
{
    private readonly List<WorkerRegistration> _workers = [];
    private readonly List<CatalogRegistration> _references = [];
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
    /// namespace and modules. Read by
    /// <see cref="ActaServiceCollectionExtensions.UseActa"/> to build the JobTypeIndex.
    /// </summary>
    internal IEnumerable<CatalogRegistration> Catalogs =>
        _references.Concat(_workers.Select(w => new CatalogRegistration(w.NamespaceName, w.Modules)));

    /// <summary>
    /// Pipeline-behavior resolvers in registration order (outermost first). Read by
    /// <see cref="ActaServiceCollectionExtensions.UseActa"/> to construct the per-worker
    /// <c>JobBehaviorPipeline</c>.
    /// </summary>
    internal IReadOnlyList<Func<IServiceProvider, IJobPipelineBehavior>> PipelineBehaviors => _pipelineBehaviors;

    /// <summary>True when <see cref="DisableCli"/> was called; suppresses the CLI host swap.</summary>
    internal bool CliDisabled { get; private set; }

    public IJobsBuilder ConfigureOptions(Action<JobsOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    public IJobsBuilder Run<TManifest>(string namespaceName, string? ownerTeam = null, string? description = null)
        where TManifest : class, IActaManifest =>
        Run(
            namespaceName,
            w =>
            {
                w.OwnerTeam = ownerTeam;
                w.Description = description;
                w.AddModule<TManifest>();
            }
        );

    public IJobsBuilder Run(string namespaceName, Action<IWorkerBuilder> configure)
    {
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkerBuilder(Services);
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
                builder.Modules,
                builder.AlertChannels,
                builder.Relay
            )
        );
        return this;
    }

    public IJobsBuilder Reference<TManifest>(string namespaceName)
        where TManifest : class, IActaManifest
    {
        namespaceName = IdentifierSyntax.CanonicalizeUserKebab(namespaceName, nameof(namespaceName));

        if (
            !_references.Any(r =>
                string.Equals(r.NamespaceName, namespaceName, StringComparison.Ordinal) && r.Modules[0].ManifestType == typeof(TManifest)
            )
        )
        {
            _references.Add(
                new CatalogRegistration(namespaceName, [new ModuleRegistration(typeof(TManifest), static () => TManifest.Descriptors)])
            );
        }
        return this;
    }

    public IJobsBuilder AddPayloadSerializer<TSerializer>()
        where TSerializer : class, IJobPayloadSerializer
    {
        Services.AddSingleton<IJobPayloadSerializer, TSerializer>();
        return this;
    }

    public IJobsBuilder UseJsonPayloads(IJsonTypeInfoResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        // Registered after UseActa's built-in json serializer; the registry is last-wins per format id.
        Services.AddSingleton<IJobPayloadSerializer>(JsonJobPayloadSerializer.WithResolver(resolver));
        return this;
    }

    public IJobsBuilder AddPipelineBehavior<TBehavior>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TBehavior : class, IJobPipelineBehavior
    {
        if (_pipelineBehaviorTypes.Add(typeof(TBehavior)))
        {
            Services.Add(new ServiceDescriptor(typeof(TBehavior), typeof(TBehavior), lifetime));
            _pipelineBehaviors.Add(static sp => sp.GetRequiredService<TBehavior>());
        }
        return this;
    }

    public IJobsBuilder DisableCli()
    {
        CliDisabled = true;
        return this;
    }
}
