using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Entry point for wiring Acta into a DI container: <c>services.UseActa(j =&gt; { j.UseSqlServer(...);
/// j.Run&lt;UsersJobs&gt;("users", ownerTeam: "growth"); })</c>, where <c>UsersJobs</c> is the source-generated manifest
/// for the project, not a class you write.
/// </summary>
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

        AddRuntimeDefaults(services);
        AddOperations(services);
        AddEngineOperations(services);
        AddExecutionServices(services);

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

        AddDeclared(services, builder);
        AddHost(services, builder);

        return services;
    }

    /// <summary>Options and their validation, the wake transport, recovery, and the payload serializers.</summary>
    private static void AddRuntimeDefaults(IServiceCollection services)
    {
        // Register the default options so consumers can resolve IOptions<JobsOptions> even when no
        // ConfigureOptions() call lands. JobsOptionsValidator enforces every per-knob and
        // coordination-invariant rule and aggregates violations; ValidateOnStart surfaces them at host
        // start rather than first .Value read.
        services.AddOptions<JobsOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JobsOptions>, JobsOptionsValidator>());

        // Swappable wake transport. InProcessWakeup is the default: same-process enqueues, control
        // verbs, and completions wake local waiters (claim loops, RunAndWaitAsync completion waits)
        // instantly; a transport package replaces the IWorkerWakeup registration for cross-process
        // reach. Publishes always go through WorkerWakeupPublisher (never breaks the caller, consistent
        // metrics); registered unconditionally so enqueue-only processes publish too.
        services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();
        services.TryAddSingleton<WorkerWakeupPublisher>();

        // One recovery pass, shared by the worker heartbeat that takes it and the test host's recovery drive.
        services.TryAddSingleton<RecoveryPass>();

        // The two built-in serializers register through TryAddEnumerable so the registry's
        // IEnumerable<IJobPayloadSerializer> ctor sees all of them (consumer apps add more by
        // registering additional implementations). Per-handler invokers and (de)serializers live on
        // the descriptor (generator-emitted), so no reflection invoker is registered.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, JsonJobPayloadSerializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, BytesJobPayloadSerializer>());
        services.TryAddSingleton<IJobPayloadSerializerRegistry, JobPayloadSerializerRegistry>();
    }

    /// <summary>
    /// The operator facade and every group with a public interface, groups in alphabetical order: each group's handlers,
    /// its shared helpers, then its operations class and interface.
    /// </summary>
    private static void AddOperations(IServiceCollection services)
    {
        // Application code injects IJobs alone; dashboards, CLIs, and operator hosts inject IActaOperations.
        services.TryAddSingleton<IActaOperations, ActaOperations>();

        services.TryAddSingleton<AcknowledgeAlertHandler>();
        services.TryAddSingleton<GetAlertableEventsHandler>();
        services.TryAddSingleton<GetAlertFloorHandler>();
        services.TryAddSingleton<GetPoisonAlertEventsHandler>();
        services.TryAddSingleton<GetAlertHandler>();
        services.TryAddSingleton<GetDeliverableAlertsHandler>();
        services.TryAddSingleton<ListAlertsHandler>();
        services.TryAddSingleton<RaiseAlertHandler>();
        services.TryAddSingleton<ResolveAlertHandler>();
        services.TryAddSingleton<ResolveAlertsHandler>();
        services.TryAddSingleton<UpdateAlertDeliveryHandler>();
        services.TryAddSingleton<AlertsOperations>();
        services.TryAddSingleton<IAlerts>(sp => sp.GetRequiredService<AlertsOperations>());

        services.TryAddSingleton<GetDefinitionContractsHandler>();
        services.TryAddSingleton<GetDefinitionHandler>();
        services.TryAddSingleton<ListDefinitionsHandler>();
        services.TryAddSingleton<RegisterDefinitionsHandler>();
        services.TryAddSingleton<ResolveDefinitionIdHandler>();
        services.TryAddSingleton<RetireDefinitionHandler>();
        services.TryAddSingleton<SetDefinitionOverridesHandler>();
        services.TryAddSingleton<DefinitionsOperations>();
        services.TryAddSingleton<IDefinitions>(sp => sp.GetRequiredService<DefinitionsOperations>());

        services.TryAddSingleton<CancelJobHandler>();
        services.TryAddSingleton<EnqueueJobHandler>();
        services.TryAddSingleton<EnqueueJobsHandler>();
        services.TryAddSingleton<GetCheckpointsHandler>();
        services.TryAddSingleton<GetInputTemplateHandler>();
        services.TryAddSingleton<GetJobExplanationHandler>();
        services.TryAddSingleton<GetJobHandler>();
        services.TryAddSingleton<GetJobIdHandler>();
        services.TryAddSingleton<GetJobInputHandler>();
        services.TryAddSingleton<GetJobLineageMapHandler>();
        services.TryAddSingleton<GetJobStatusHandler>();
        services.TryAddSingleton<GetResultHandler>();
        services.TryAddSingleton<PauseJobHandler>();
        services.TryAddSingleton<PurgeJobHandler>();
        services.TryAddSingleton<ReprioritizeJobHandler>();
        services.TryAddSingleton<RescheduleJobHandler>();
        services.TryAddSingleton<ResetJobStateHandler>();
        services.TryAddSingleton<ResolveJobIdByDeduplicationKeyHandler>();
        services.TryAddSingleton<ResolveJobIdByRefHandler>();
        services.TryAddSingleton<ResolveSlotIdHandler>();
        services.TryAddSingleton<RestartJobHandler>();
        services.TryAddSingleton<ResumeJobHandler>();
        services.TryAddSingleton<UpdateJobInputHandler>();
        services.TryAddSingleton<JobRequestRouting>();
        services.TryAddSingleton<JobsOperations>();
        services.TryAddSingleton<IJobs>(sp => sp.GetRequiredService<JobsOperations>());

        services.TryAddSingleton<GetOverviewHandler>();
        services.TryAddSingleton<ListEventsHandler>();
        services.TryAddSingleton<ListJobsHandler>();
        services.TryAddSingleton<LedgerOperations>();
        services.TryAddSingleton<ILedger>(sp => sp.GetRequiredService<LedgerOperations>());

        services.TryAddSingleton<ListNamespacesHandler>();
        services.TryAddSingleton<ResumeNamespaceHandler>();
        services.TryAddSingleton<SuspendNamespaceHandler>();
        services.TryAddSingleton<UpdateNamespaceHandler>();
        services.TryAddSingleton<NamespacesOperations>();
        services.TryAddSingleton<INamespaces>(sp => sp.GetRequiredService<NamespacesOperations>());

        services.TryAddSingleton<ConsumeOutboxSignalHandler>();
        services.TryAddSingleton<DiscardOutboxHandler>();
        services.TryAddSingleton<GetOutboxSignalHandler>();
        services.TryAddSingleton<ListOutboxQuarantinedHandler>();
        services.TryAddSingleton<ListOutboxSourcesHandler>();
        services.TryAddSingleton<ParkOutboxSignalHandler>();
        services.TryAddSingleton<PlanOutboxSignalHandler>();
        services.TryAddSingleton<RequeueOutboxHandler>();
        services.TryAddSingleton<OutboxOperations>();
        services.TryAddSingleton<IOutbox>(sp => sp.GetRequiredService<OutboxOperations>());

        services.TryAddSingleton<GetLiveSchedulesHandler>();
        services.TryAddSingleton<GetScheduleStateHandler>();
        services.TryAddSingleton<GetDueSchedulesHandler>();
        services.TryAddSingleton<GetScheduleHorizonHandler>();
        services.TryAddSingleton<AdmitFiringsHandler>();
        services.TryAddSingleton<ListSchedulesHandler>();
        services.TryAddSingleton<PauseScheduleHandler>();
        services.TryAddSingleton<PreviewScheduleHandler>();
        services.TryAddSingleton<RegisterSchedulesHandler>();
        services.TryAddSingleton<ResolveScheduleTargetHandler>();
        services.TryAddSingleton<ResumeScheduleHandler>();
        services.TryAddSingleton<TriggerScheduleNowHandler>();
        services.TryAddSingleton<SchedulesOperations>();
        services.TryAddSingleton<ISchedules>(sp => sp.GetRequiredService<SchedulesOperations>());

        services.TryAddSingleton<GetTagsHandler>();
        services.TryAddSingleton<RemoveTagsHandler>();
        services.TryAddSingleton<ReplaceTagsHandler>();
        services.TryAddSingleton<ResolveTagTargetHandler>();
        services.TryAddSingleton<UpsertTagsHandler>();
        services.TryAddSingleton<TagsOperations>();
        services.TryAddSingleton<ITags>(sp => sp.GetRequiredService<TagsOperations>());

        services.TryAddSingleton<GetTenantHandler>();
        services.TryAddSingleton<GetTenantNameHandler>();
        services.TryAddSingleton<ListTenantsHandler>();
        services.TryAddSingleton<RegisterTenantHandler>();
        services.TryAddSingleton<ResumeTenantHandler>();
        services.TryAddSingleton<SuspendTenantHandler>();
        services.TryAddSingleton<UpdateTenantHandler>();
        services.TryAddSingleton<TenantsOperations>();
        services.TryAddSingleton<ITenants>(sp => sp.GetRequiredService<TenantsOperations>());

        services.TryAddSingleton<ExtendWorkerLeasesHandler>();
        services.TryAddSingleton<GetWorkerHandler>();
        services.TryAddSingleton<ListWorkersHandler>();
        services.TryAddSingleton<MarkDeadWorkersHandler>();
        services.TryAddSingleton<StartWorkerHandler>();
        services.TryAddSingleton<StopWorkerHandler>();
        services.TryAddSingleton<WorkersOperations>();
        services.TryAddSingleton<IWorkers>(sp => sp.GetRequiredService<WorkersOperations>());
    }

    /// <summary>The groups only the engine calls, in the same order as <see cref="AddOperations"/>.</summary>
    private static void AddEngineOperations(IServiceCollection services)
    {
        services.TryAddSingleton<GetUtcNowHandler>();
        services.TryAddSingleton<ClockOperations>();
        services.AddSingleton<IClockOperations>(sp => sp.GetRequiredService<ClockOperations>());
        services.TryAddSingleton<IActaClock>(sp => sp.GetRequiredService<IClockOperations>());

        services.TryAddSingleton<AccessCheckpointHandler>();
        services.TryAddSingleton<ArmOrConsumeSleepTimerHandler>();
        services.TryAddSingleton<ClaimJobHandler>();
        services.TryAddSingleton<ClaimJobsHandler>();
        services.TryAddSingleton<CompleteExecutionsHandler>();
        services.TryAddSingleton<CompleteStepHandler>();
        services.TryAddSingleton<GetChildJobIdsHandler>();
        services.TryAddSingleton<GetStaleChildLatchesHandler>();
        services.TryAddSingleton<ReclaimStuckJobsHandler>();
        services.TryAddSingleton<GetFiringHandler>();
        services.TryAddSingleton<RecordNoteHandler>();
        services.TryAddSingleton<StartStepHandler>();
        services.TryAddSingleton<ExecutionOperations>();

        services.TryAddSingleton<AcquireLockHandler>();
        services.TryAddSingleton<AcquireSlotHandler>();
        services.TryAddSingleton<ReleaseLockHandler>();
        services.TryAddSingleton<ReserveRateHandler>();
        services.TryAddSingleton<LocksOperations>();

        services.TryAddSingleton<PurgeExpiredDataHandler>();
        services.TryAddSingleton<MaintenanceOperations>();

        services.TryAddSingleton<RaiseSignalHandler>();
        services.TryAddSingleton<WaitSignalHandler>();
        services.TryAddSingleton<SignalsOperations>();
    }

    /// <summary>Metrics, the alert transports, and the ambient JobContext.</summary>
    private static void AddExecutionServices(IServiceCollection services)
    {
        // Consumers light up the meter with WithMetrics(m => m.AddMeter(ActaMetrics.MeterName)).
        services.TryAddSingleton<ActaMetrics>();

        // Swappable alert transports: the delivery loop resolves one from the channel's transport
        // kind. The log transport is always available for defaults and tests, Slack is the built-in
        // real transport, and consumer apps can add more implementations.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertTransport, LogAlertTransport>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAlertTransport, SlackAlertTransport>());
        services.TryAddSingleton<IAlertTransportRegistry, AlertTransportRegistry>();

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
    }

    /// <summary>What the builder declared: alert channels and routing, the enqueue indexes, the pipeline, the workers, and outbox relays.</summary>
    private static void AddDeclared(IServiceCollection services, ActaBuilder builder)
    {
        services.TryAddSingleton(new AlertChannelRegistry(builder.Workers));

        // Worker startup checks each namespace's alert routing against the declared channels.
        services.TryAddSingleton(sp => new AlertRoutingCheck(
            sp.GetRequiredService<AlertChannelRegistry>(),
            sp.GetRequiredService<IOptions<JobsOptions>>(),
            sp.GetService<ILogger<AlertRoutingCheck>>()
        ));

        // Type to enqueue-route index backing the typed IJobs.EnqueueAsync<TInput> and RunAndWaitAsync
        // facade. The raw EnqueueJobRequest path needs no index. Captured here because the namespace
        // is assigned per Reference/Run call, not on the namespace-neutral manifest.
        var typeRegistry = JobTypeRegistry.Build(builder.Catalogs);
        services.AddSingleton(typeRegistry);

        // Contract index: (manifest type, job name) -> route, backing the IJobs overloads that take a
        // generated JobCall. Same catalogs as JobTypeRegistry.
        var contractRegistry = JobContractRegistry.Build(builder.Catalogs);
        services.AddSingleton(contractRegistry);

        // Descriptor index: (namespace, job name) -> generated descriptor, backing IJobs.GetInputTemplate.
        // Same catalogs again, so an enqueue-only host answers for every job it references.
        services.AddSingleton(JobDescriptorRegistry.Build(builder.Catalogs));

        // Each behavior type was registered scoped by AddPipelineBehavior; this singleton holds only
        // the order and never captures a scope, so per-attempt resolution runs against the attempt
        // scope inside JobBehaviorPipeline.Build.
        services.AddSingleton(new JobBehaviorPipeline(builder.PipelineBehaviors.ToArray()));

        // One WorkerRuntime per declared worker: a process running several j.Run(...) calls fans out
        // one claim/dispatch/heartbeat trio per namespace. WorkerRuntime.Create binds the per-worker
        // WorkerRegistration; the rest of each runtime's collaborators resolve as shared singletons.
        foreach (var worker in builder.Workers)
        {
            services.AddSingleton(sp => WorkerRuntime.Create(sp, worker));
        }

        // Per-namespace outbox relay resolution: sys.outbox, executing under a namespace, resolves ITS
        // registration + a source and relay service bound to it (schema/table/factory/threshold) from the
        // declared workers. Registered only when a relay exists, so a non-relay process stays untouched.
        if (builder.Workers.Any(w => w.Relay is not null))
        {
            services.AddSingleton(sp => new OutboxRelayRegistry(
                builder.Workers,
                sp.GetRequiredService<JobsOperations>(),
                sp.GetService<ILoggerFactory>()
            ));
        }
    }

    /// <summary>The process host: a CLI verb when the command line asks for one, else the worker host.</summary>
    private static void AddHost(IServiceCollection services, ActaBuilder builder)
    {
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
    }
}
