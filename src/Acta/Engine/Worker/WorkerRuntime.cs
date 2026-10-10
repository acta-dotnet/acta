using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Worker-runtime singleton owning everything for its namespace: the catalog upsert in <see cref="InitializeAsync"/>,
/// the claim/dispatch pipeline in <see cref="RunLoopAsync"/>, the lease heartbeat, and the host-level
/// <see cref="RunAsync"/> that runs pipeline and heartbeat concurrently. A thin facade over <see cref="WorkerContext"/>,
/// <see cref="WorkerRuntimeInitializer"/>, <see cref="JobClaimer"/>, <see cref="JobExecutor"/>, and
/// <see cref="JobFinalizer"/>. An enqueue-only runtime skips the worker row and the pipeline but still bootstraps.
/// </summary>
internal sealed class WorkerRuntime
{
    private readonly WorkerContext _context;
    private readonly InFlightJobRegistry _registry;
    private readonly WorkerRuntimeInitializer _initializer;
    private readonly JobExecutor _executor;
    private readonly JobFinalizer _finalizer;
    private readonly JobClaimer _claimer;
    private readonly int _executorCount;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly RecoverySlotMonitor _recoveryMonitor;

    /// <summary>
    /// Cancelled by BeginDrainAsync to stop the claimer while the heartbeat and
    /// in-flight handlers run on under the host token. Lives for the runtime's lifetime so the
    /// drain signal is never lost to a startup race; a lightweight source with no timer and no
    /// WaitHandle needs no disposal (see the CA1001 suppression above).
    /// </summary>
    private readonly CancellationTokenSource _drainCts = new();

    public WorkerRuntime(
        Locks locks,
        IActaClock clock,
        IJobPayloadSerializerRegistry serializers,
        IServiceProvider rootServices,
        IOptions<JobsOptions> options,
        JobBehaviorPipeline pipeline,
        WorkerRegistration? workerRegistration = null,
        ILogger<WorkerRuntime>? log = null,
        ActaMetrics? metrics = null,
        IWorkerWakeup? wakeup = null,
        WorkerWakeupPublisher? wakeupPublisher = null,
        AlertRoutingCheck? alertRouting = null
    )
    {
        var logger = log ?? NullLogger<WorkerRuntime>.Instance;
        _context = new WorkerContext(workerRegistration);
        _registry = new InFlightJobRegistry(options.Value);
        _executorCount = options.Value.MaxConcurrentExecutors;
        WorkerNamespaceName = workerRegistration?.NamespaceName;

        // Direct-ctor (test seam) callers get a private in-process wakeup pair; the composition root
        // passes the container singletons so enqueue publishes reach this loop.
        var wake = wakeup ?? new InProcessWakeup();
        var publisher = wakeupPublisher ?? new WorkerWakeupPublisher(wake, metrics: metrics);

        // The in-flight gauge reads this worker's entries by state on collection. Enqueue-only
        // runtimes have no registration and contribute no source.
        if (workerRegistration is { } registration)
        {
            metrics?.AddInFlightSource(registration.NamespaceName, _registry.Count);
        }

        _initializer = new WorkerRuntimeInitializer(
            new CatalogRegistrar(
                rootServices.GetRequiredService<RegisterDefinitionsHandler>(),
                rootServices.GetRequiredService<GetDefinitionContractsHandler>(),
                rootServices.GetRequiredService<GetScheduleStateHandler>(),
                rootServices.GetRequiredService<RegisterSchedulesHandler>(),
                rootServices.GetRequiredService<Execution>(),
                clock,
                serializers,
                alertRouting,
                options,
                _context,
                logger
            ),
            rootServices.GetRequiredService<WorkerLifecycle>(),
            rootServices.GetRequiredService<IServerClock>(),
            options,
            workerRegistration,
            _context,
            logger
        );

        _finalizer = new JobFinalizer(
            rootServices.GetRequiredService<Execution>(),
            rootServices.GetRequiredService<CancelJobHandler>(),
            publisher,
            options.Value,
            _context,
            _registry,
            logger,
            metrics
        );
        _executor = new JobExecutor(
            locks,
            clock,
            serializers,
            rootServices,
            options.Value,
            pipeline,
            _context,
            _registry,
            _finalizer,
            logger,
            metrics
        );
        _claimer = new JobClaimer(
            rootServices.GetRequiredService<Execution>(),
            _executor,
            options.Value,
            _context,
            _registry,
            wake,
            logger,
            metrics
        );
        _heartbeat = new WorkerHeartbeat(
            rootServices.GetRequiredService<WorkerLifecycle>(),
            rootServices.GetRequiredService<GetDefinitionContractsHandler>(),
            options,
            workerRegistration,
            _context,
            _registry,
            logger
        );
        _recoveryMonitor = new RecoverySlotMonitor(
            rootServices.GetRequiredService<Execution>(),
            publisher,
            workerRegistration,
            _context,
            logger,
            // RunOnceAsync claims and executes without taking an executor permit, which is what lets the
            // sweep run on a worker whose executors are all busy.
            (namespaceName, slotJobId, token) => _executor.RunOnceAsync(namespaceName, slotJobId, token)
        );
    }

    /// <summary>
    /// Composition-root factory: binds the per-worker registration to the shared singleton
    /// collaborators with no reflection. The public constructor remains the direct test seam.
    /// </summary>
    public static WorkerRuntime Create(IServiceProvider sp, WorkerRegistration worker) =>
        new(
            sp.GetRequiredService<Locks>(),
            sp.GetRequiredService<IActaClock>(),
            sp.GetRequiredService<IJobPayloadSerializerRegistry>(),
            sp,
            sp.GetRequiredService<IOptions<JobsOptions>>(),
            sp.GetRequiredService<JobBehaviorPipeline>(),
            worker,
            sp.GetService<ILogger<WorkerRuntime>>(),
            sp.GetService<ActaMetrics>(),
            sp.GetRequiredService<IWorkerWakeup>(),
            sp.GetRequiredService<WorkerWakeupPublisher>(),
            sp.GetRequiredService<AlertRoutingCheck>()
        );

    /// <summary>This worker's declared namespace; null for an enqueue-only runtime.</summary>
    public string? WorkerNamespaceName { get; }

    public IReadOnlyDictionary<string, int> RegisteredNamespaceIds => _context.RegisteredNamespaceIds;

    /// <summary>The recurring slot jobs this worker registered or recognized on claim.</summary>
    internal ICollection<long> RecurringSlotJobIds => _context.RecurringSlotJobIds.Keys;

    public bool TryGetDefinitionId(string namespaceName, string jobName, out int definitionId) =>
        _context.TryGetDefinitionId(namespaceName, jobName, out definitionId);

    public Task InitializeAsync(CancellationToken ct) => _initializer.InitializeAsync(ct);

    /// <summary>
    /// Records this worker's clean shutdown: marks its <c>workers</c> row Stopped and emits
    /// <c>worker.stopped</c>. Driven by the host on graceful stop; a no-op for enqueue-only runtimes.
    /// </summary>
    public Task StopAsync(CancellationToken ct) => _initializer.StopAsync(ct);

    /// <summary>
    /// Host entry point: runs the claim/dispatch pipeline and the worker heartbeat concurrently under the same
    /// <paramref name="ct"/>. The host runs <see cref="InitializeAsync"/> during startup, before this is reached. Every
    /// loop self-gates on <see cref="WorkerRegistration"/>, so enqueue-only deployments fall through immediately.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        // The claimer and the recovery monitor's checks stop on _drainCts (graceful drain) or ct (hard stop); the
        // heartbeat and in-flight execution run on ct, so a drain finishes in-flight work, including a recovery pass
        // the monitor started, before DrainCompletion completes.
        var pipeline = RunPipelineAsync(ct, _drainCts.Token);
        var monitor = _recoveryMonitor.RunAsync(ct, _drainCts.Token);
        DrainCompletion = Task.WhenAll(pipeline, monitor);
        return Task.WhenAll(DrainCompletion, _heartbeat.RunAsync(ct));
    }

    public Task RunLoopAsync(CancellationToken ct) => RunPipelineAsync(ct, ct);

    /// <summary>
    /// The claimer, the executor loops, and the finalizer loops; ends once the claimer stopped, the executors emptied
    /// its channel, and the finalizers wrote every completion the executors handed them.
    /// </summary>
    private async Task RunPipelineAsync(CancellationToken ct, CancellationToken drainCt)
    {
        if (WorkerNamespaceName is null)
        {
            return;
        }

        var executors = Enumerable.Range(0, _executorCount).Select(_ => _executor.RunLoopAsync(ct)).ToArray();
        var finalizers = Enumerable.Range(0, JobFinalizer.LoopCount).Select(_ => _finalizer.RunLoopAsync(ct)).ToArray();
        await _claimer.RunAsync(ct, drainCt);
        await Task.WhenAll(executors);
        _registry.Completed.Writer.TryComplete();
        await Task.WhenAll(finalizers);
    }

    /// <summary>
    /// Begins a graceful drain: stops the claim loop from taking new work and stamps the worker Draining via
    /// the heartbeat, while in-flight handlers run to completion under the host token. The host awaits
    /// <see cref="DrainCompletion"/>, then <see cref="StopAsync"/> stamps Stopped. A no-op for an enqueue-only
    /// runtime. Intake is stopped first - the must-not-fail step; the Draining stamp is observability and may
    /// fail on a transient blip without breaking the drain.
    /// </summary>
    public Task BeginDrainAsync(CancellationToken ct)
    {
        BeginDrain();
        return StampDrainingAsync(ct);
    }

    /// <summary>Stops intake and enters in-memory draining mode without performing I/O.</summary>
    public void BeginDrain()
    {
        _drainCts.Cancel();
        _heartbeat.BeginDrain();
    }

    /// <summary>Persists Draining after <see cref="BeginDrain"/> has stopped intake.</summary>
    public Task StampDrainingAsync(CancellationToken ct) => _heartbeat.StampDrainingAsync(ct);

    /// <summary>
    /// Completes when the claim/dispatch pipeline has drained - every claimed and in-flight job finished, not
    /// just the live attempt count sampled to zero. The signal the host awaits to bound a graceful drain;
    /// already-completed (so instant) before <see cref="RunAsync"/> starts and for an enqueue-only runtime.
    /// </summary>
    public Task DrainCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>The claims this runtime holds, from claim to completion write; exposed for observability and drain assertions.</summary>
    public int InFlightCount => _registry.Count();

    /// <summary>
    /// This runtime's live descriptor index, keyed by <c>definitions.id</c>: what this deployment can
    /// actually run, which is not what the namespace holds jobs for. The heartbeat's policy reload rewrites
    /// entries here while executors read them, and an entry a deployment dropped stays dropped.
    /// </summary>
    internal ConcurrentDictionary<int, JobDescriptor> Descriptors => _context.DescriptorByDefinitionId;

    /// <summary>
    /// The definition ids this runtime has bounced for want of a handler and no longer claims, as the
    /// array its claims bind.
    /// </summary>
    internal int[] UnsupportedDefinitionIdsSnapshot => _context.UnsupportedDefinitionIdsSnapshot;

    /// <summary>
    /// Drops a definition from the excluded set so this runtime claims it again. A test seam: a
    /// production process that carries the handler is a different process.
    /// </summary>
    internal bool ForgetUnsupportedDefinition(int definitionId) => _context.ForgetUnsupportedDefinition(definitionId);

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the concurrency-key lock). Deterministic single-shot primitive for loop and tests.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, CancellationToken ct) => _executor.RunOnceAsync(namespaceName, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (the claim's <c>ExplicitJobIds</c> path). One tick, no
    /// retry: a transiently-locked row (READPAST) yields <see cref="RunOnceStatus.NothingClaimed"/>.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        _executor.RunOnceAsync(namespaceName, jobId, ct);

    /// <summary>
    /// Run exactly one heartbeat pass: renew the held job leases and locks, re-arm their attempts' lease deadlines, and
    /// cancel handlers whose job or lock was lost. Drives deterministic tests and the CLI's jobs debug pump.
    /// </summary>
    public Task RunHeartbeatOnceAsync(CancellationToken ct) => _heartbeat.TickAsync(ct);
}
