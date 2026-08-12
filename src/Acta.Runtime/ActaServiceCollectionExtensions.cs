using Acta.Runtime.Cli;
using Acta.Runtime.Configuration;
using Acta.Runtime.Hosting;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Alerting;
using Acta.Runtime.Modules.Alerting.Api;
using Acta.Runtime.Modules.Execution;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Namespaces;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Settings;
using Acta.Runtime.Modules.Execution.Signals;
using Acta.Runtime.Modules.Execution.Tenants;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Modules.Operations;
using Acta.Runtime.Modules.Operations.Events;
using Acta.Runtime.Modules.Operations.Overview;
using Acta.Runtime.Modules.Operations.Tags;
using Acta.Runtime.Modules.Outbox;
using Acta.Runtime.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Entry point for wiring Acta into a DI container.
/// </summary>
/// <example>
/// <code>
/// services.UseActa(j =&gt;
/// {
///     j.UseSqlServer(sql =&gt; sql.ConnectionString = config.GetConnectionString("acta")!);
///     // UsersJobs is the source-generated manifest for this project, not a class you write.
///     j.Run&lt;UsersJobs&gt;(namespaceName: "users", ownerTeam: "growth");
/// });
/// </code>
/// </example>
public static class ActaServiceCollectionExtensions
{
    /// <summary>Test seam: when non-null, supplies the detected CLI args instead of the process command line.</summary>
    internal static Func<string[]?>? CliArgsOverride;

    /// <summary>
    /// Registers Acta core services and applies the builder callback. A durable provider must be
    /// selected inside the callback (<c>j.UseSqlServer(...)</c>, <c>j.UsePostgres(...)</c>, or the
    /// embedded single-node <c>j.UseSqlite(...)</c>); a missing provider fails fast with
    /// <see cref="InvalidOperationException"/> on return.
    /// </summary>
    public static IServiceCollection UseActa(this IServiceCollection services, Action<IActaBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Register the default options so consumers can resolve IOptions<JobsOptions> even when no
        // ConfigureOptions() call lands. JobsOptionsValidator enforces every per-knob and
        // coordination-invariant rule and aggregates violations; ValidateOnStart surfaces them at host
        // start rather than first .Value read.
        services.AddOptions<JobsOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JobsOptions>, JobsOptionsValidator>());

        // The swappable lock store (leases-backed ILockStore) and the DB-backed UTC clock are
        // registered by the selected SQL provider over its own connection; a Redis-backed ILockStore
        // or a deterministic test IActaClock can replace them via a prior registration.

        // Swappable wake transport. InProcessWakeup is the default: same-process enqueues, control
        // verbs, and completions wake local waiters (claim loops, ExecuteAndWaitAsync completion waits)
        // instantly; a transport package replaces the IWorkerWakeup registration for cross-process
        // reach. Publishes always go through WorkerWakeupPublisher (never breaks the caller, consistent
        // metrics); registered unconditionally so enqueue-only processes publish too.
        services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();
        services.TryAddSingleton<WorkerWakeupPublisher>();

        // Slice runtime services: payload serializer registry, descriptor index, and the thin
        // IJobs surface. The three built-in serializers register through TryAddEnumerable so the
        // registry's IEnumerable<IJobPayloadSerializer> ctor sees all of them (consumer apps add
        // more by registering additional IJobPayloadSerializer implementations). Per-handler
        // invokers and (de)serializers live on the descriptor (generator-emitted), so no reflection
        // invoker is registered.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, JsonJobPayloadSerializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, BytesJobPayloadSerializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, TextJobPayloadSerializer>());
        services.TryAddSingleton<IJobPayloadSerializerRegistry, JobPayloadSerializerRegistry>();
        services.TryAddSingleton<IJobs, JobsApi>();

        // Feature services and the property-only store composite. Providers register the store ports
        // themselves (e.g. IOverviewStore); the composite grows one property per migrated feature.
        services.TryAddSingleton<OverviewService>();
        services.TryAddSingleton<EventsService>();
        services.TryAddSingleton<DefinitionsService>();
        services.TryAddSingleton<NamespacesService>();
        services.TryAddSingleton<TenantsService>();
        services.TryAddSingleton<TenantKeyCache>();
        services.TryAddSingleton<TagsService>();
        services.TryAddSingleton<JobsService>();
        // Execution's declared read API for the Operations module (tags target resolution, the
        // operator job list); the same JobsService instance behind IJobs serves it.
        services.TryAddSingleton<IExecutionQueries>(static sp => sp.GetRequiredService<JobsService>());
        services.TryAddSingleton<SignalService>();
        services.TryAddSingleton<IAlertSink, AlertStoreSink>();

        // The operator facade and its module-owned domain facades. Application code injects IJobs
        // alone; dashboards, CLIs, and operator hosts inject IActaOperations. Each domain facade is
        // registered here (the composition root may see every module) but owned by its module.
        services.TryAddSingleton<ISchedules, SchedulesApi>();
        services.TryAddSingleton<IDefinitions, DefinitionsApi>();
        services.TryAddSingleton<IWorkers, WorkersApi>();
        services.TryAddSingleton<IAlerts, AlertsApi>();
        services.TryAddSingleton<ITenants, TenantsApi>();
        services.TryAddSingleton<INamespaces, NamespacesApi>();
        services.TryAddSingleton<ITags>(static sp => sp.GetRequiredService<TagsService>());
        services.TryAddSingleton<SettingsService>();
        services.TryAddSingleton<ISettings, SettingsApi>();
        services.TryAddSingleton<IActaOperations, OperationsApi>();

        // Process-wide Acta meter. One singleton owns the instruments; every worker runtime emits
        // into it. Consumers light it up with WithMetrics(m => m.AddMeter(JobMetrics.MeterName)).
        services.TryAddSingleton<JobMetrics>();

        // Swappable alert transports: the delivery loop resolves one from the channel's transport
        // kind. The log transport is always available for defaults and tests, Slack is the built-in
        // real transport, and consumer apps can add more implementations.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertTransport, LogAlertTransport>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertTransport, SlackAlertTransport>());
        services.TryAddSingleton<IAlertTransportRegistry, AlertTransportRegistry>();

        // Alerts are raised in-handler via ctx.AlertAsync and by the framework's automatic failure
        // alerts; there is no out-of-handler operator alert verb on IJobs (minimum operator surface).

        // Ambient JobContext for DI-resolved handlers (e.g. MediatR IRequestHandlers and pipeline
        // behaviors that can't take a JobContext method parameter): the runtime sets the accessor at
        // the top of each attempt scope; JobContext resolves from it. Scoped so concurrent executors,
        // each with its own attempt scope, never cross context.
        services.TryAddScoped<IJobContextAccessor, JobContextAccessor>();
        services.TryAddScoped<JobContext>(sp =>
            sp.GetRequiredService<IJobContextAccessor>().JobContext
            ?? throw new InvalidOperationException(
                "JobContext is only resolvable inside a job-handler attempt; it is unavailable on the root provider and on enqueue-only paths."
            )
        );

        var builder = new ActaBuilder(services);
        configure(builder);

        // Fail fast at configuration time unless exactly one durable provider was selected. Official
        // provider extensions reject conflicts before adding their provider-specific graph; this final
        // check also protects internal/custom registrations that bypass those extensions.
        var providerRegistrations = ActaProviderRegistration.FindAll(services);
        if (providerRegistrations.Count == 0)
        {
            throw new InvalidOperationException(
                "UseActa requires a durable provider. Call j.UseSqlServer(...), j.UsePostgres(...), or j.UseSqlite(...) inside the configure callback."
            );
        }
        if (providerRegistrations.Count != 1)
        {
            throw new InvalidOperationException(
                $"UseActa requires exactly one durable provider, but found {providerRegistrations.Count}: {string.Join(", ", providerRegistrations)}."
            );
        }

        services.TryAddSingleton<IAlertChannelRegistry>(new AlertChannelRegistry(builder.Workers));

        // Execution's startup routing seam, implemented by Alerting: worker init validates each
        // namespace's alert routing through this Execution-owned port, keeping the module edge
        // Alerting -> Execution.Api only.
        services.TryAddSingleton<IAlertRoutingCheck>(sp => new AlertRoutingCheck(
            sp.GetRequiredService<IAlertChannelRegistry>(),
            sp.GetRequiredService<IOptions<JobsOptions>>(),
            sp.GetService<ILogger<AlertRoutingCheck>>()
        ));

        // Type to enqueue-route index backing the typed IJobs.EnqueueAsync<TInput> and ExecuteAndWaitAsync facade.
        // Built from the declared catalogs: Reference contributes routes without a worker, Run contributes
        // its worker's modules. The raw JobEnqueueRequest path needs no index. Captured here because the
        // namespace is assigned per Reference/Run call, not on the namespace-neutral manifest.
        var typeIndex = JobTypeIndex.Build(builder.Catalogs);
        services.AddSingleton(typeIndex);

        // Contract index: (manifest type, job name) -> route, backing the explicit-target
        // IJobs.EnqueueAsync(JobContract<TInput>, ...) overloads. Same catalogs as JobTypeIndex.
        var contractIndex = JobContractIndex.Build(builder.Catalogs);
        services.AddSingleton(contractIndex);

        // Descriptor index: (namespace, job name) -> generated descriptor, backing IJobs.GetInputTemplate.
        // Same catalogs again, so an enqueue-only host answers for every job it references.
        services.AddSingleton(JobDescriptorIndex.Build(builder.Catalogs));

        // Pipeline behaviors: the ordered resolver list captured on the builder (outermost first),
        // snapshotted into the per-worker JobExecution's fold. Each behavior type was registered scoped by
        // AddPipelineBehavior; this singleton holds only the order and never captures a scope, so
        // per-attempt resolution runs against the attempt scope inside JobBehaviorPipeline.Build.
        services.AddSingleton(new JobBehaviorPipeline(builder.PipelineBehaviors.ToArray()));

        // One WorkerRuntime per declared worker: a process running several j.Run(...) calls fans out
        // one claim/dispatch/heartbeat trio per namespace. WorkerRuntime.Create binds the per-worker
        // WorkerRegistration; the rest of each runtime's collaborators resolve as shared singletons.
        foreach (var worker in builder.Workers)
        {
            services.AddSingleton(sp => WorkerRuntime.Create(sp, worker));
        }

        // Per-namespace outbox relay resolution: sys.outbox, executing under a namespace, resolves ITS
        // registration + a source store/service bound to it (schema/table/factory/threshold) from the
        // declared workers. Registered only when a relay exists, so a non-relay process stays untouched.
        if (builder.Workers.Any(w => w.Relay is not null))
        {
            services.AddSingleton(sp => new OutboxRelayRegistry(
                builder.Workers,
                sp.GetRequiredService<IJobSubmission>(),
                sp.GetService<ILoggerFactory>()
            ));
        }

        // CLI mode: a process started as `myapp jobs <verb> ...` runs the verb and exits instead of
        // booting the worker. The swap replaces the worker host; WorkerRuntime singletons stay
        // registered so the debug verb can run one job in-process. Opt out via j.DisableCli().
        var cliArgs = CliArgsOverride is { } over ? over() : CliCommandParser.DetectInvocation(Environment.GetCommandLineArgs());
        if (cliArgs is not null && !builder.CliDisabled)
        {
            var namespaces = builder.Catalogs.Select(c => c.NamespaceName).Distinct(StringComparer.Ordinal).ToArray();
            services.AddSingleton(new CliInvocation(cliArgs, namespaces));
            services.AddHostedService<CliCommandHost>();
        }
        else
        {
            // The process host runs provider bootstrap once, then every worker's RunAsync; always
            // registered so an enqueue-only process (no workers) still bootstraps the schema before any
            // enqueue resolves.
            services.AddHostedService<WorkerRuntimeHost>();
        }

        return services;
    }
}
