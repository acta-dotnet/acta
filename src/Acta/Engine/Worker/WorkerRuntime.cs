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
/// <see cref="WorkerRuntimeInitializer"/>, <see cref="WorkerClaimer"/>, <see cref="WorkerExecutor"/>, and
/// <see cref="WorkerFinalizer"/>. An enqueue-only runtime skips the worker row and the pipeline but still bootstraps.
/// </summary>
internal sealed class WorkerRuntime
{
    private readonly WorkerContext _context;
    private readonly InFlightJobRegistry _registry;
    private readonly WorkerRuntimeInitializer _initializer;
    private readonly WorkerExecutor _executor;
    private readonly WorkerFinalizer _finalizer;
    private readonly WorkerClaimer _claimer;
    private readonly int _executorCount;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly ScheduleAdmitter _admitter;

    /// <summary>
    /// Cancelled by BeginDrain to stop the claimer and the admitter while the heartbeat and
    /// in-flight handlers run on until the host stops. Lives for the runtime's lifetime so the
    /// drain signal is never lost to a startup race; a lightweight source with no timer and no
    /// WaitHandle needs no disposal (the CA1001 ruling in .editorconfig).
    /// </summary>
    private readonly CancellationTokenSource _drainCts = new();

    public WorkerRuntime(
        LocksOperations locks,
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
                rootServices.GetRequiredService<DefinitionsOperations>(),
                rootServices.GetRequiredService<SchedulesOperations>(),
                clock,
                serializers,
                alertRouting,
                options,
                _context,
                rootServices.GetRequiredService<CalendarsOperations>(),
                logger
            ),
            rootServices.GetRequiredService<WorkersOperations>(),
            rootServices.GetRequiredService<IClockOperations>(),
            options,
            workerRegistration,
            _context,
            logger
        );

        _finalizer = new WorkerFinalizer(
            rootServices.GetRequiredService<ExecutionOperations>(),
            rootServices.GetRequiredService<JobsOperations>(),
            publisher,
            options.Value,
            _context,
            _registry,
            logger,
            metrics
        );
        _executor = new WorkerExecutor(
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
        _claimer = new WorkerClaimer(
            rootServices.GetRequiredService<ExecutionOperations>(),
            options.Value,
            _context,
            _registry,
            wake,
            logger,
            metrics
        );
        _admitter = new ScheduleAdmitter(
            rootServices.GetRequiredService<SchedulesOperations>(),
            clock,
            _context,
            wake,
            publisher,
            options.Value,
            rootServices.GetRequiredService<CalendarsOperations>(),
            logger,
            metrics
        );
        _heartbeat = new WorkerHeartbeat(
            rootServices.GetRequiredService<WorkersOperations>(),
            rootServices.GetRequiredService<DefinitionsOperations>(),
            options,
            workerRegistration,
            _context,
            _registry,
            rootServices.GetRequiredService<RecoveryPass>(),
            logger,
            metrics
        );
    }

    /// <summary>
    /// Composition-root factory: binds the per-worker registration to the shared singleton
    /// collaborators with no reflection. The public constructor remains the direct test seam.
    /// </summary>
    public static WorkerRuntime Create(IServiceProvider sp, WorkerRegistration worker) =>
        new(
            sp.GetRequiredService<LocksOperations>(),
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

    public bool TryGetDefinitionId(string namespaceName, string jobName, out int definitionId) =>
        _context.TryGetDefinitionId(namespaceName, jobName, out definitionId);

    public Task InitializeAsync(CancellationToken ct) => _initializer.InitializeAsync(ct);

    /// <summary>
    /// Records this worker's clean shutdown: marks its <c>workers</c> row Stopped and emits
    /// <c>worker.stopped</c>. Driven by the host on graceful stop; a no-op for enqueue-only runtimes.
    /// </summary>
    public Task StopAsync(CancellationToken ct) => _initializer.StopAsync(ct);

    /// <summary>
    /// Host entry point: runs the claim/dispatch pipeline, the worker heartbeat, and the schedule admitter concurrently
    /// under the same <paramref name="ct"/>. The host runs <see cref="InitializeAsync"/> during startup, before this is
    /// reached. Every loop self-gates on <see cref="WorkerRegistration"/>, so enqueue-only deployments fall through
    /// immediately. A loop that faults stops the others, so the fault reaches the host instead of leaving a worker that
    /// heartbeats without claiming.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var intake = CancellationTokenSource.CreateLinkedTokenSource(_drainCts.Token, stop.Token);
        // The claimer stops on intake (a graceful drain or a stop) or on stop; the heartbeat, the recovery pass it
        // takes, and in-flight execution run on stop, so a drain finishes in-flight work before DrainCompletion completes.
        var pipeline = RunPipelineAsync(stop, intake.Token);
        DrainCompletion = pipeline;
        // The admitter stops with the claimer on a drain: a draining host admits nothing new.
        var admission = WorkerNamespaceName is null
            ? Task.CompletedTask
            : StopOnFaultAsync(_admitter.RunAsync(WorkerNamespaceName, intake.Token), stop);
        await Task.WhenAll(pipeline, StopOnFaultAsync(_heartbeat.RunAsync(stop.Token), stop), admission);
    }

    /// <summary>
    /// Awaits <paramref name="loop"/>, and when it faults while <paramref name="stop"/> is still live, cancels it so the
    /// sibling loops end, then rethrows the fault. A loop ended by that cancellation, or by the host's, is not a fault.
    /// </summary>
    internal static async Task StopOnFaultAsync(Task loop, CancellationTokenSource stop)
    {
        try
        {
            await loop;
        }
        catch (Exception) when (!stop.IsCancellationRequested)
        {
            await stop.CancelAsync();
            throw;
        }
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await RunPipelineAsync(stop, stop.Token);
    }

    /// <summary>
    /// The claimer, the executor loops, and the finalizer; ends once the claimer stopped, the executors emptied
    /// its channel, and the finalizer wrote every completion the executors handed it. Each loop runs on
    /// <paramref name="stop"/>, so one that faults stops the rest, and every loop is joined before this ends.
    /// </summary>
    private async Task RunPipelineAsync(CancellationTokenSource stop, CancellationToken drainCt)
    {
        if (WorkerNamespaceName is null)
        {
            return;
        }

        var ct = stop.Token;
        var executors = Enumerable.Range(0, _executorCount).Select(_ => StopOnFaultAsync(_executor.RunLoopAsync(ct), stop)).ToArray();
        // One finalizer: while it writes a batch, everything the executors hand over queues behind it and is the
        // next batch, so the write falling behind is what forms the batches.
        var finalizer = StopOnFaultAsync(_finalizer.RunLoopAsync(ct), stop);
        var feeders = Task.WhenAll([StopOnFaultAsync(_claimer.RunAsync(ct, drainCt), stop), .. executors]);
        // The finalizer's queue closes once everything that feeds it has ended, faulted or not.
        await feeders.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _registry.Completed.Writer.TryComplete();
        await Task.WhenAll(feeders, finalizer);
    }

    /// <summary>
    /// Begins a graceful drain without I/O: stops the claim loop from taking new work while in-flight handlers run to
    /// completion under the host token. The host awaits <see cref="DrainCompletion"/>, then <see cref="StopAsync"/>
    /// stamps Stopped.
    /// </summary>
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
    /// This runtime's live descriptor index, keyed by <c>definitions.definition_id</c>: what this deployment can
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
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the concurrency-key lock). Deterministic single-shot primitive for loop and tests.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, CancellationToken ct) => _executor.RunOnceAsync(namespaceName, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (through <c>claim_job</c>). One tick, no
    /// retry: a transiently-locked row (READPAST) yields <see cref="RunOnceStatus.NothingClaimed"/>.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        _executor.RunOnceAsync(namespaceName, jobId, ct);

    /// <summary>
    /// Run exactly one heartbeat pass: renew the held job leases and locks, re-arm their attempts' lease deadlines, and
    /// cancel handlers whose job or lock was lost. Drives deterministic tests and the CLI's jobs debug pump.
    /// </summary>
    public Task RunHeartbeatOnceAsync(CancellationToken ct) => _heartbeat.TickAsync(ct);

    /// <summary>
    /// Run exactly one admission pass over <paramref name="namespaceName"/>: evaluate every slot with a due schedule,
    /// admit what the policies select, and park the rest. Drives deterministic tests; the host runs it in a loop.
    /// </summary>
    public Task<AdmissionPass> AdmitOnceAsync(string namespaceName, CancellationToken ct) => _admitter.AdmitOnceAsync(namespaceName, ct);
}
