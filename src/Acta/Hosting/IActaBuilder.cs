using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Acta;

/// <summary>
/// Configuration surface returned by <c>services.UseActa(...)</c>. Provider packages extend it through
/// <c>this IActaBuilder</c> extension methods (<c>j.UseSqlServer(...)</c>, <c>j.UsePostgres(...)</c>, or the embedded
/// <c>j.UseSqlite(...)</c> for the durable provider). Workers (<c>Run</c>), payload serializers
/// (<see cref="AddPayloadSerializer{TSerializer}"/>), and options (<see cref="ConfigureOptions"/>) are interface
/// members rather than extensions, so the fluent chain has a stable, single-assembly contract.
/// </summary>
public interface IActaBuilder
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
    IActaBuilder ConfigureOptions(Action<JobsOptions> configure);

    /// <summary>
    /// Declare a worker that claims <paramref name="namespaceName"/> and hosts the single manifest
    /// <typeparamref name="TManifest"/>, shorthand for the lambda overload with one <c>AddManifest</c> call. A process
    /// may host several workers, each owning a distinct namespace (call <c>Run</c> once per worker); two workers for
    /// the same namespace throw at DI build. An enqueue-only runtime calls <see cref="Reference{TManifest}"/> instead.
    /// </summary>
    IActaBuilder Run<TManifest>(string namespaceName, string? ownerTeam = null, string? description = null)
        where TManifest : class, IJobManifest;

    /// <summary>
    /// Declare a worker that claims <paramref name="namespaceName"/> and hosts the manifests declared on the
    /// <see cref="IWorkerBuilder"/>. At initialization the framework upserts the <c>namespaces</c> row (with the
    /// owner team and description when supplied), upserts every declared manifest's <c>definitions</c> rows under
    /// it, and inserts a <c>workers</c> row. A process may host several workers, each owning a distinct namespace;
    /// two workers for the same namespace throw at DI build.
    /// </summary>
    IActaBuilder Run(string namespaceName, Action<IWorkerBuilder> configure);

    /// <summary>
    /// Make <typeparamref name="TManifest"/>'s jobs typed-enqueueable under <paramref name="namespaceName"/> without
    /// running a worker in this process: the enqueue-only counterpart of <c>Run</c>, which feeds the typed
    /// <c>IJobs.EnqueueAsync</c> route index but registers no worker, writes no catalog rows, and never claims jobs.
    /// The namespace's worker owns migrations and definition registration, so start it once before the first
    /// enqueue. <c>Run</c> implies Reference for its own namespace.
    /// </summary>
    IActaBuilder Reference<TManifest>(string namespaceName)
        where TManifest : class, IJobManifest;

    /// <summary>
    /// Opt this host out of the automatic jobs CLI: a process started with the first argument
    /// "jobs" then boots normally instead of running the CLI verb and exiting. For apps that
    /// own their command-line surface.
    /// </summary>
    IActaBuilder DisableCli();

    /// <summary>
    /// Register an additional <see cref="IJobPayloadSerializer"/>. The serializer's
    /// <see cref="JobPayloadFormat.Id"/> joins it to descriptor metadata; runtime payload columns
    /// resolve their serializer at dispatch via
    /// <see cref="IJobPayloadSerializerRegistry"/>.
    /// </summary>
    IActaBuilder AddPayloadSerializer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSerializer>()
        where TSerializer : class, IJobPayloadSerializer;

    /// <summary>
    /// Wire an app-supplied source-generated JSON resolver (a <c>JsonSerializerContext</c>) for job
    /// payloads, so payload (de)serialization needs no reflection under Native AOT. It overrides the
    /// built-in reflection-based JSON serializer for the <c>json</c> format. Under reflection-off the
    /// resolver must cover every job input/output (and durable variable/signal/step) type; the
    /// recommended AOT path is typed enqueue, which routes through this serializer.
    /// </summary>
    IActaBuilder UseJsonPayloads(IJsonTypeInfoResolver resolver);

    /// <summary>
    /// Register an <see cref="IJobPipelineBehavior"/> that wraps every handler invocation: behaviors run in
    /// registration order, first outermost, and registering a type twice is a no-op. Each resolves from the
    /// per-attempt DI scope, so it may take constructor-injected dependencies including the scoped
    /// <see cref="JobContext"/>; it runs inside the attempt, after start and before complete, and must not own durable
    /// state. The default <see cref="ServiceLifetime.Scoped"/> is one instance per attempt, which a behavior needing
    /// <see cref="JobContext"/> requires; a stateless one may pass <see cref="ServiceLifetime.Singleton"/>.
    /// </summary>
    IActaBuilder AddPipelineBehavior<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TBehavior>(
        ServiceLifetime lifetime = ServiceLifetime.Scoped
    )
        where TBehavior : class, IJobPipelineBehavior;

    /// <summary>
    /// Register the calendar a <c>[Schedule(name, Calendar = ..., CalendarVersion = ...)]</c> binds: one immutable
    /// implementation per dotted kebab-case name and version, so a schedule names the exact version it wants and two
    /// versions coexist across a migration. Registering one name and version twice with different instances throws.
    /// Every host that evaluates or previews the schedule resolves the calendar in its own process, so register
    /// calendars in the configuration the workers and the API host share.
    /// </summary>
    IActaBuilder AddCalendar(string name, IActaCalendar implementation);

    /// <summary>
    /// Register an availability calendar built at startup, which a <c>[Schedule(..., Availability = name)]</c> binds
    /// under the rules of <see cref="AddCalendar"/>.
    /// </summary>
    IActaBuilder AddAvailabilityCalendar(string name, IActaAvailabilityCalendar implementation);
}
