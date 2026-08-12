using Acta.Runtime.Hosting;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Worker-runtime singleton. Owns everything for its namespace: catalog upsert (namespace +
/// definitions + <c>workers</c>) in <see cref="InitializeAsync"/> (driven by the host during
/// startup), the claim/dispatch poll loop in <see cref="RunLoopAsync"/>, the lease heartbeat, and the
/// host-level <see cref="RunAsync"/> entry point that runs the loop and the heartbeat concurrently.
/// </summary>
/// <remarks>
/// A thin facade over the runtime collaborators it composes from its constructor dependencies: the
/// <see cref="WorkerContext"/> (shared state), <see cref="WorkerRuntimeInitializer"/> (catalog
/// upsert), <see cref="JobExecutor"/> + <see cref="JobExecution"/> (claimed-job execution),
/// and <see cref="WorkerLoop"/> (the claim/dispatch poll loop). Enqueue-only runtimes (a
/// <c>j.Reference&lt;...&gt;(...)</c> host with no <c>j.Run&lt;...&gt;(...)</c> worker)
/// skip the worker-row write and short-circuit the loop, but still run provider bootstraps so
/// the schema is in place before any enqueue resolves.
/// </remarks>
internal sealed class WorkerRuntime
{
    private readonly WorkerContext _context;
    private readonly WorkerRuntimeInitializer _initializer;
    private readonly JobExecutor _executor;
    private readonly WorkerLoop _loop;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly LockLeaseHeartbeat _lockHeartbeat;
    private readonly AttemptWatchdog _watchdog;
    private readonly DefinitionPolicyReloader _policyReloader;

    // Cancelled by BeginDrainAsync to stop the claim loop's producer while the heartbeat and in-flight
    // handlers run on under the host token. Lives for the runtime's lifetime so the drain signal is never
    // lost to a startup race; a lightweight source with no timer/registration needs no disposal.
    private readonly CancellationTokenSource _drainCts = new();

    // The claim/dispatch loop task, captured so the host can await the actual drain (all claimed + in-flight
    // work finished), not a sampled in-flight count that misses channel-buffered claims.

    public WorkerRuntime(
        ActaProviderInfo provider,
        ILockStore lockStore,
        IActaClock clock,
        IJobPayloadSerializerRegistry serializers,
        IServiceProvider rootServices,
        IOptions<JobsOptions> options,
        JobBehaviorPipeline pipeline,
        WorkerRegistration? workerRegistration = null,
        ILogger<WorkerRuntime>? log = null,
        JobMetrics? metrics = null,
        IWorkerWakeup? wakeup = null,
        WorkerWakeupPublisher? wakeupPublisher = null,
        IAlertRoutingCheck? alertRouting = null
    )
    {
        var logger = log ?? NullLogger<WorkerRuntime>.Instance;
        _context = new WorkerContext(workerRegistration);
        WorkerNamespaceName = workerRegistration?.NamespaceName;

        // Direct-ctor (test seam) callers get a private in-process wakeup pair; the composition root
        // passes the container singletons so enqueue publishes reach this loop.
        var wake = wakeup ?? new InProcessWakeup();
        var publisher = wakeupPublisher ?? new WorkerWakeupPublisher(wake, metrics: metrics);

        // The executing gauge reads this worker's live in-flight count on collection. Enqueue-only
        // runtimes have no registration and contribute no source.
        if (workerRegistration is { } registration)
        {
            metrics?.AddExecutingSource(registration.NamespaceName, () => _context.RunningAttempts.Count);
        }

        _initializer = new WorkerRuntimeInitializer(
            rootServices.GetRequiredService<DefinitionsService>(),
            rootServices.GetRequiredService<IDefinitionStore>(),
            rootServices.GetRequiredService<IScheduleStore>(),
            rootServices.GetRequiredService<IWorkerStore>(),
            clock,
            rootServices.GetRequiredService<IServerClock>(),
            serializers,
            alertRouting,
            options,
            workerRegistration,
            _context,
            logger
        );

        // One per-runtime completion buffer, shared by the execution (which buffers plain terminal
        // completions under the Bulk profile) and the loop (which runs the flusher). Inert on other
        // profiles: nothing is ever enqueued and the flusher is never started. Only routine providers
        // (SQL Server, Postgres) get a sink - Bulk degrades to Direct on inline-only providers (SQLite),
        // which have no batched-completion routine.
        var completionSink = provider.SupportsRoutines
            ? new CompletionSink(rootServices.GetRequiredService<IExecutionStore>(), publisher, options, logger, metrics)
            : null;

        var jobExecution = new JobExecution(
            rootServices.GetRequiredService<IJobStore>(),
            rootServices.GetRequiredService<IExecutionStore>(),
            serializers,
            options,
            pipeline,
            publisher,
            logger,
            metrics,
            completionSink
        );
        _executor = new JobExecutor(lockStore, clock, serializers, rootServices, options, _context, jobExecution, logger, metrics);
        _loop = new WorkerLoop(
            rootServices.GetRequiredService<IExecutionStore>(),
            _executor,
            options,
            workerRegistration,
            _context,
            wake,
            logger,
            metrics,
            completionSink
        );
        _heartbeat = new WorkerHeartbeat(rootServices.GetRequiredService<IWorkerStore>(), options, workerRegistration, _context, logger);
        _lockHeartbeat = new LockLeaseHeartbeat(lockStore, options, workerRegistration, _context, logger);
        _watchdog = new AttemptWatchdog(options, workerRegistration, _context, logger);
        _policyReloader = new DefinitionPolicyReloader(
            rootServices.GetRequiredService<IDefinitionStore>(),
            options,
            workerRegistration,
            _context,
            logger
        );
    }

    /// <summary>
    /// Composition-root factory: binds the per-worker registration to the shared singleton
    /// collaborators with no reflection. The public constructor remains the direct test seam.
    /// </summary>
    public static WorkerRuntime Create(IServiceProvider sp, WorkerRegistration worker) =>
        new(
            sp.GetRequiredService<ActaProviderInfo>(),
            sp.GetRequiredService<ILockStore>(),
            sp.GetRequiredService<IActaClock>(),
            sp.GetRequiredService<IJobPayloadSerializerRegistry>(),
            sp,
            sp.GetRequiredService<IOptions<JobsOptions>>(),
            sp.GetRequiredService<JobBehaviorPipeline>(),
            worker,
            sp.GetService<ILogger<WorkerRuntime>>(),
            sp.GetService<JobMetrics>(),
            sp.GetRequiredService<IWorkerWakeup>(),
            sp.GetRequiredService<WorkerWakeupPublisher>(),
            sp.GetRequiredService<IAlertRoutingCheck>()
        );

    /// <summary>This worker's declared namespace; null for an enqueue-only runtime.</summary>
    public string? WorkerNamespaceName { get; }

    public IReadOnlyDictionary<string, short> RegisteredNamespaceIds => _context.RegisteredNamespaceIds;

    public bool TryGetDefinitionId(string namespaceName, string jobName, out int definitionId) =>
        _context.TryGetDefinitionId(namespaceName, jobName, out definitionId);

    public Task InitializeAsync(CancellationToken ct) => _initializer.InitializeAsync(ct);

    /// <summary>
    /// Records this worker's clean shutdown: marks its <c>workers</c> row Stopped and emits
    /// <c>worker.stopped</c>. Driven by the host on graceful stop; a no-op for enqueue-only runtimes.
    /// </summary>
    public Task StopAsync(CancellationToken ct) => _initializer.StopAsync(ct);

    /// <summary>
    /// Host entry point: runs the claim/dispatch loop, the worker heartbeat, the lock-lease heartbeat, the
    /// lease watchdog, and the policy reloader concurrently under the same <paramref name="ct"/>. The host
    /// runs <see cref="InitializeAsync"/> during startup, before this is reached. Every loop self-gates on
    /// <see cref="WorkerRegistration"/>, so enqueue-only deployments fall through immediately.
    /// </summary>
    public Task RunAsync(CancellationToken ct)
    {
        // The loop's producer stops on _drainCts (graceful drain) or ct (hard stop); the renewers, watchdog
        // and in-flight execution run on ct, so a drain finishes in-flight work before the loop returns. The
        // loop task is held so the host can await that drain via DrainCompletion.
        DrainCompletion = _loop.RunLoopAsync(ct, _drainCts.Token);
        return Task.WhenAll(
            DrainCompletion,
            _heartbeat.RunAsync(ct),
            _lockHeartbeat.RunAsync(ct),
            _watchdog.RunAsync(ct),
            _policyReloader.RunAsync(ct)
        );
    }

    public Task RunLoopAsync(CancellationToken ct) => _loop.RunLoopAsync(ct);

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
    /// Completes when the claim/dispatch loop has drained - every claimed and in-flight job finished, not
    /// just the live attempt count sampled to zero. The signal the host awaits to bound a graceful drain;
    /// already-completed (so instant) before <see cref="RunAsync"/> starts and for an enqueue-only runtime.
    /// </summary>
    public Task DrainCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>This runtime's live in-flight attempt count; exposed for observability and drain assertions.</summary>
    public int InFlightCount => _context.RunningAttempts.Count;

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the exclusive-key lock). Deterministic single-shot primitive for loop and tests.
    /// </summary>
    public Task<RunOnceOutcome> RunOnceAsync(string namespaceName, CancellationToken ct) => _executor.RunOnceAsync(namespaceName, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (the claim's <c>ExplicitJobIds</c> path). One tick, no
    /// retry: a transiently-locked row (READPAST) yields <see cref="RunOnceOutcome.NothingClaimed"/>.
    /// </summary>
    public Task<RunOnceOutcome> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        _executor.RunOnceAsync(namespaceName, jobId, ct);

    /// <summary>
    /// Run exactly one full heartbeat pass: renew this worker's batched job leases and feed their deadlines
    /// (worker heartbeat), extend every held lock and feed theirs (lock heartbeat), then enforce deadlines
    /// (watchdog) - cancelling handlers whose job or lock was lost or whose lease has run down. The three
    /// loops run independently in production; this drives them in sequence for deterministic tests.
    /// </summary>
    public async Task RunHeartbeatOnceAsync(CancellationToken ct)
    {
        await _heartbeat.TickAsync(ct);
        await _lockHeartbeat.TickAsync(ct);
        await _watchdog.TickAsync(ct);
    }

    /// <summary>
    /// Run exactly one definition-policy reload pass: re-overlay the effective (override-or-default)
    /// policy onto this worker's live descriptor index for every definition changed since the last sweep.
    /// The deterministic single-shot the reload loop drives per tick; tests use it to observe an operator
    /// override taking effect without a restart.
    /// </summary>
    public Task RunDefinitionReloadOnceAsync(CancellationToken ct) =>
        WorkerNamespaceName is { } ns ? _policyReloader.TickAsync(ns, ct) : Task.CompletedTask;
}
