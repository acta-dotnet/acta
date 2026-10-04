using Acta.Runtime;
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

        // Swappable wake transport. InProcessWakeup is the default: same-process enqueues, control
        // verbs, and completions wake local waiters (claim loops, RunAndWaitAsync completion waits)
        // instantly; a transport package replaces the IWorkerWakeup registration for cross-process
        // reach. Publishes always go through WorkerWakeupPublisher (never breaks the caller, consistent
        // metrics); registered unconditionally so enqueue-only processes publish too.
        services.TryAddSingleton<IWorkerWakeup, InProcessWakeup>();
        services.TryAddSingleton<WorkerWakeupPublisher>();

        // One recovery pass, shared by the sys.recovery handler and the test host's recovery drive.
        services.TryAddSingleton<RecoveryPass>();

        // The three built-in serializers register through TryAddEnumerable so the registry's
        // IEnumerable<IJobPayloadSerializer> ctor sees all of them (consumer apps add more by
        // registering additional implementations). Per-handler invokers and (de)serializers live on
        // the descriptor (generator-emitted), so no reflection invoker is registered.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, JsonJobPayloadSerializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, BytesJobPayloadSerializer>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobPayloadSerializer, TextJobPayloadSerializer>());
        services.TryAddSingleton<IJobPayloadSerializerRegistry, JobPayloadSerializerRegistry>();

        services.TryAddSingleton<TenantKeyCache>();

        // The operator facade over the group facades. Application code injects IJobs alone; dashboards,
        // CLIs, and operator hosts inject IActaOperations.
        services.TryAddSingleton<IActaOperations, ActaOperations>();

        // Operation groups that have moved to handlers: each group's handlers, then its facade.
        services.AddSingleton<IServerClock, GetUtcNowHandler>();
        services.TryAddSingleton<IActaClock>(sp => sp.GetRequiredService<IServerClock>());
        services.TryAddSingleton<GetSettingHandler>();
        services.TryAddSingleton<SetSettingHandler>();
        services.TryAddSingleton<ISettings, Settings>();
        services.TryAddSingleton<RegisterTenantHandler>();
        services.TryAddSingleton<GetTenantHandler>();
        services.TryAddSingleton<GetTenantKeyHandler>();
        services.TryAddSingleton<ListTenantsHandler>();
        services.TryAddSingleton<SuspendTenantHandler>();
        services.TryAddSingleton<ResumeTenantHandler>();
        services.TryAddSingleton<UpdateTenantHandler>();
        services.TryAddSingleton<ITenants, Tenants>();
        services.TryAddSingleton<ListNamespacesHandler>();
        services.TryAddSingleton<ListNamespaceItemsHandler>();
        services.TryAddSingleton<SuspendNamespaceHandler>();
        services.TryAddSingleton<ResumeNamespaceHandler>();
        services.TryAddSingleton<UpdateNamespaceHandler>();
        services.TryAddSingleton<INamespaces, Namespaces>();
        services.TryAddSingleton<AcquireLockHandler>();
        services.TryAddSingleton<AcquireSlotHandler>();
        services.TryAddSingleton<ReserveRateHandler>();
        services.TryAddSingleton<ExtendLockHandler>();
        services.TryAddSingleton<ReleaseLockHandler>();
        services.TryAddSingleton<ILocks, Locks>();
        services.TryAddSingleton<PurgeExpiredDataHandler>();
        services.TryAddSingleton<RetentionCoordinator>();
        services.TryAddSingleton<TagTargetResolution>();
        services.TryAddSingleton<GetTagsHandler>();
        services.TryAddSingleton<ReplaceTagsHandler>();
        services.TryAddSingleton<UpsertTagsHandler>();
        services.TryAddSingleton<RemoveTagsHandler>();
        services.TryAddSingleton<ITags, Tags>();
        services.TryAddSingleton<StartWorkerHandler>();
        services.TryAddSingleton<StopWorkerHandler>();
        services.TryAddSingleton<ExtendWorkerLeasesHandler>();
        services.TryAddSingleton<MarkDeadWorkersHandler>();
        services.TryAddSingleton<IWorkerLifecycle, WorkerLifecycle>();
        services.TryAddSingleton<GetWorkerHandler>();
        services.TryAddSingleton<ListWorkersHandler>();
        services.TryAddSingleton<IWorkers, Workers>();
        services.TryAddSingleton<GetDefinitionContractsHandler>();
        services.TryAddSingleton<RegisterJobDefinitionsHandler>();
        services.TryAddSingleton<DefinitionKeyResolution>();
        services.TryAddSingleton<GetJobDefinitionHandler>();
        services.TryAddSingleton<ListJobDefinitionsHandler>();
        services.TryAddSingleton<SetJobDefinitionOverridesHandler>();
        services.TryAddSingleton<RetireJobDefinitionHandler>();
        services.TryAddSingleton<IDefinitions, Definitions>();
        services.TryAddSingleton<ListJobsHandler>();
        services.TryAddSingleton<ListEventsHandler>();
        services.TryAddSingleton<GetOverviewHandler>();
        services.TryAddSingleton<ILedger, Ledger>();
        services.TryAddSingleton<RaiseJobAlertHandler>();
        services.TryAddSingleton<GetAlertableEventsHandler>();
        services.TryAddSingleton<GetDeliverableAlertsHandler>();
        services.TryAddSingleton<UpdateAlertDeliveryHandler>();
        services.TryAddSingleton<ResolveJobAlertsHandler>();
        services.TryAddSingleton<IAlertLifecycle, AlertLifecycle>();
        services.TryAddSingleton<AcknowledgeJobAlertHandler>();
        services.TryAddSingleton<ResolveJobAlertManualHandler>();
        services.TryAddSingleton<GetJobAlertHandler>();
        services.TryAddSingleton<ListJobAlertsHandler>();
        services.TryAddSingleton<IAlerts, Alerts>();
        services.TryAddSingleton<GetLiveSchedulesHandler>();
        services.TryAddSingleton<GetScheduleStateHandler>();
        services.TryAddSingleton<RegisterScheduledJobsHandler>();
        services.TryAddSingleton<ScheduleTargetResolution>();
        services.TryAddSingleton<PauseScheduleHandler>();
        services.TryAddSingleton<ResumeScheduleHandler>();
        services.TryAddSingleton<SetScheduleOverridesHandler>();
        services.TryAddSingleton<TriggerScheduleNowHandler>();
        services.TryAddSingleton<ListJobSchedulesHandler>();
        services.TryAddSingleton<PreviewScheduleHandler>();
        services.TryAddSingleton<ISchedules, Schedules>();
        services.TryAddSingleton<ParkOutboxSignalHandler>();
        services.TryAddSingleton<GetOutboxSignalHandler>();
        services.TryAddSingleton<ConsumeOutboxSignalHandler>();
        services.TryAddSingleton<RecordOutboxEventHandler>();
        services.TryAddSingleton<OutboxInbox>();
        services.TryAddSingleton<ListOutboxSourcesHandler>();
        services.TryAddSingleton<ListOutboxQuarantinedHandler>();
        services.TryAddSingleton<RequeueOutboxHandler>();
        services.TryAddSingleton<DiscardOutboxHandler>();
        services.TryAddSingleton<IOutbox, Outbox>();
        services.TryAddSingleton<ResolveJobIdByRefHandler>();
        services.TryAddSingleton<ResolveJobIdByDeduplicationKeyHandler>();
        services.TryAddSingleton<JobLookupResolution>();
        services.TryAddSingleton<GetJobHandler>();
        services.TryAddSingleton<GetJobStatusHandler>();
        services.TryAddSingleton<GetJobInputHandler>();
        services.TryAddSingleton<GetJobResultHandler>();
        services.TryAddSingleton<GetJobCheckpointsHandler>();
        services.TryAddSingleton<GetJobExplanationHandler>();
        services.TryAddSingleton<GetJobLineageMapHandler>();
        services.TryAddSingleton<CancelJobHandler>();
        services.TryAddSingleton<PauseJobHandler>();
        services.TryAddSingleton<ResumeJobHandler>();
        services.TryAddSingleton<RestartJobHandler>();
        services.TryAddSingleton<RescheduleJobHandler>();
        services.TryAddSingleton<ReprioritizeJobHandler>();
        services.TryAddSingleton<UpdateJobInputHandler>();
        services.TryAddSingleton<PurgeJobHandler>();
        services.TryAddSingleton<ResetJobStateHandler>();
        services.TryAddSingleton<EnqueueOneHandler>();
        services.TryAddSingleton<EnqueueBatchHandler>();
        services.TryAddSingleton<GetInputTemplateHandler>();
        services.TryAddSingleton<JobRequestRouting>();
        services.TryAddSingleton<JobRunAndWait>();
        services.TryAddSingleton<IJobs, Jobs>();
        services.TryAddSingleton<RaiseSignalHandler>();
        services.TryAddSingleton<WaitSignalHandler>();
        services.TryAddSingleton<ISignals, Signals>();
        services.TryAddSingleton<ClaimBatchHandler>();
        services.TryAddSingleton<ClaimOneHandler>();
        services.TryAddSingleton<StartExecutionHandler>();
        services.TryAddSingleton<CompleteExecutionHandler>();
        services.TryAddSingleton<ReclaimStuckJobsHandler>();
        services.TryAddSingleton<RepairRecoverySlotHandler>();
        services.TryAddSingleton<StartStepHandler>();
        services.TryAddSingleton<CompleteStepHandler>();
        services.TryAddSingleton<CheckpointSlotHandler>();
        services.TryAddSingleton<RecordJobNoteHandler>();
        services.TryAddSingleton<GetChildJobIdsHandler>();
        services.TryAddSingleton<GetStaleChildLatchesHandler>();
        services.TryAddSingleton<ArmOrConsumeSleepTimerHandler>();
        services.TryAddSingleton<IExecution, Execution>();

        // Consumers light up the meter with WithMetrics(m => m.AddMeter(JobMetrics.MeterName)).
        services.TryAddSingleton<JobMetrics>();

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

        // Worker startup checks each namespace's alert routing against the declared channels.
        services.TryAddSingleton(sp => new AlertRoutingCheck(
            sp.GetRequiredService<IAlertChannelRegistry>(),
            sp.GetRequiredService<IOptions<JobsOptions>>(),
            sp.GetService<ILogger<AlertRoutingCheck>>()
        ));

        // Type to enqueue-route index backing the typed IJobs.EnqueueAsync<TInput> and RunAndWaitAsync
        // facade. The raw JobEnqueueRequest path needs no index. Captured here because the namespace
        // is assigned per Reference/Run call, not on the namespace-neutral manifest.
        var typeIndex = JobTypeIndex.Build(builder.Catalogs);
        services.AddSingleton(typeIndex);

        // Contract index: (manifest type, job name) -> route, backing the explicit-target
        // IJobs.EnqueueAsync(JobContract<TInput>, ...) overloads. Same catalogs as JobTypeIndex.
        var contractIndex = JobContractIndex.Build(builder.Catalogs);
        services.AddSingleton(contractIndex);

        // Descriptor index: (namespace, job name) -> generated descriptor, backing IJobs.GetInputTemplate.
        // Same catalogs again, so an enqueue-only host answers for every job it references.
        services.AddSingleton(JobDescriptorIndex.Build(builder.Catalogs));

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
