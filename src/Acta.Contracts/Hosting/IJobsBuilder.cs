using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Acta;

/// <summary>
/// Configuration surface returned by <c>services.UseActa(...)</c>. Provider packages extend
/// this via <c>this IJobsBuilder</c> extension methods (<c>j.UseSqlServer(...)</c>,
/// <c>j.UsePostgres(...)</c>, or the embedded <c>j.UseSqlite(...)</c> for the durable provider).
/// Workers (<c>Run</c>), payload serializers
/// (<see cref="AddPayloadSerializer{TSerializer}"/>), and options (<see cref="ConfigureOptions"/>)
/// are interface members rather than extensions so the fluent chain has a stable, single-assembly
/// contract.
/// </summary>
public interface IJobsBuilder
{
    /// <summary>
    /// The underlying service collection; provider packages register their concrete
    /// implementations against it.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// Configure the cluster-wide <see cref="JobsOptions"/>. Multiple calls compose (Options
    /// pattern delta-merges).
    /// </summary>
    IJobsBuilder ConfigureOptions(Action<JobsOptions> configure);

    /// <summary>
    /// Declare a worker that claims <paramref name="namespaceName"/> and hosts the single module
    /// <typeparamref name="TManifest"/>, shorthand for the lambda overload with one <c>AddModule</c>
    /// call.
    /// </summary>
    /// <remarks>
    /// A process may host several workers, each owning a distinct namespace (call <c>Run</c> once
    /// per worker). Registering two workers for the same namespace throws at DI build. An
    /// enqueue-only runtime calls <see cref="Reference{TManifest}"/> instead of <c>Run</c>.
    /// </remarks>
    IJobsBuilder Run<TManifest>(string namespaceName, string? ownerTeam = null, string? description = null)
        where TManifest : class, IActaManifest;

    /// <summary>
    /// Declare a worker that claims <paramref name="namespaceName"/> and hosts the modules declared
    /// on the <see cref="IWorkerBuilder"/>.
    /// </summary>
    /// <remarks>
    /// At initialization the framework upserts the <c>namespaces</c> row (carrying the worker's
    /// owner-team + description when supplied), upserts every declared manifest's
    /// <c>definitions</c> rows under that namespace, and INSERTs a <c>workers</c> row. A
    /// process may host several workers, each owning a distinct namespace; registering two workers
    /// for the same namespace throws at DI build.
    /// </remarks>
    IJobsBuilder Run(string namespaceName, Action<IWorkerBuilder> configure);

    /// <summary>
    /// Make <typeparamref name="TManifest"/>'s jobs typed-enqueueable under
    /// <paramref name="namespaceName"/> without running a worker in this process.
    /// </summary>
    /// <remarks>
    /// Reference is the enqueue-only counterpart of <c>Run</c>: it feeds the typed
    /// <c>IJobs.EnqueueAsync</c> route index but registers no worker, writes no catalog rows, and
    /// never claims jobs. The namespace's worker owns migrations and definition registration; start
    /// it once before the first enqueue. <c>Run</c> implies Reference for its own namespace.
    /// </remarks>
    IJobsBuilder Reference<TManifest>(string namespaceName)
        where TManifest : class, IActaManifest;

    /// <summary>
    /// Opt this host out of the automatic jobs CLI: a process started with the first argument
    /// "jobs" then boots normally instead of running the CLI verb and exiting. For apps that
    /// own their command-line surface.
    /// </summary>
    IJobsBuilder DisableCli();

    /// <summary>
    /// Register an additional <see cref="IJobPayloadSerializer"/>. The serializer's
    /// <see cref="JobPayloadFormat.Id"/> joins it to descriptor metadata; runtime payload columns
    /// resolve their serializer at dispatch via
    /// <see cref="IJobPayloadSerializerRegistry"/>.
    /// </summary>
    IJobsBuilder AddPayloadSerializer<TSerializer>()
        where TSerializer : class, IJobPayloadSerializer;

    /// <summary>
    /// Wire an app-supplied source-generated JSON resolver (a <c>JsonSerializerContext</c>) for job
    /// payloads, so payload (de)serialization needs no reflection under Native AOT. It overrides the
    /// built-in reflection-based JSON serializer for the <c>json</c> format. Under reflection-off the
    /// resolver must cover every job input/output (and durable variable/signal/step) type; the
    /// recommended AOT path is typed enqueue, which routes through this serializer.
    /// </summary>
    IJobsBuilder UseJsonPayloads(IJsonTypeInfoResolver resolver);

    /// <summary>
    /// Register an <see cref="IJobPipelineBehavior"/> that wraps every handler invocation. Behaviors run
    /// in registration order: the first registered is the outermost link, the last is closest to the
    /// handler.
    /// </summary>
    /// <remarks>
    /// Each behavior is resolved from the per-attempt dependency-injection scope, so it may take
    /// constructor-injected dependencies (including the scoped <see cref="JobContext"/>). A behavior runs
    /// inside the attempt, after start and before complete, and must not own durable state. Registering
    /// the same behavior type twice is a no-op (the first registration's position wins). The default
    /// lifetime is <see cref="ServiceLifetime.Scoped"/>, one instance per attempt, which a behavior
    /// needing the scoped <see cref="JobContext"/> requires; a stateless behavior with no scoped
    /// dependencies may pass <see cref="ServiceLifetime.Singleton"/> for one process-wide instance.
    /// </remarks>
    IJobsBuilder AddPipelineBehavior<TBehavior>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TBehavior : class, IJobPipelineBehavior;
}
