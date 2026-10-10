using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Executes one already-claimed job up to its completion: starts a Dispatched claim, resolves the descriptor, opens
/// the per-attempt DI scope, plans a recurring slot fire, builds the <see cref="RuntimeJobContext"/> and publishes it
/// on the scope, then hands the admit-invoke body to <see cref="JobExecution"/>. Its loops take what
/// <see cref="JobClaimer"/> claimed and hand each completion to <see cref="JobFinalizer"/>, so an executor is free as
/// soon as the completion is built. A claim this deployment carries no handler for completes as a hand-back to Ready.
/// </summary>
internal sealed class JobExecutor(
    Locks locks,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    IServiceProvider rootServices,
    IOptions<JobsOptions> options,
    JobBehaviorPipeline pipeline,
    WorkerContext context,
    InFlightJobRegistry registry,
    JobFinalizer finalizer,
    ILogger? log = null,
    ActaMetrics? metrics = null
)
{
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly long _handBackDelayMs = (long)options.Value.SafetyPollInterval.TotalMilliseconds;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly WorkerContext _context = context;
    private readonly InFlightJobRegistry _registry = registry;
    private readonly Execution _execution = rootServices.GetRequiredService<Execution>();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ActaMetrics? _metrics = metrics;
    private readonly JobExecution _jobExecution = new(serializers, options, pipeline, log ?? NullLogger.Instance);
    private readonly AttemptContextFactory _contexts = new(
        rootServices,
        context,
        clock,
        serializers,
        locks,
        options,
        log ?? NullLogger.Instance,
        metrics
    );

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the concurrency-key lock). The deterministic single-shot primitive, holding no
    /// executor permit.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, explicitJobId: null, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (via the claim's <c>ExplicitJobIds</c> path),
    /// for callers that already know which job to run. Still a single tick: the claim uses READPAST, so
    /// a transiently-locked row yields <see cref="RunOnceStatus.NothingClaimed"/>, and the caller decides
    /// whether to tick again.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, jobId, ct);

    private async Task<RunOnceStatus> RunOnceCoreAsync(string namespaceName, long? explicitJobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        var (namespaceId, workerId) = _context.ResolveWorker(namespaceName);

        // Only the scanning claim carries the exclusion: a caller naming a job id asked for that row,
        // and gets the bounce rather than an empty claim it cannot tell from a locked-away one.
        var claim = explicitJobId is { } id
            ? await _execution.ClaimOneAsync(new ClaimOptions(namespaceId, workerId, MaxBatch: 1), _leaseTtlSeconds, id, ct)
            : await _execution.ClaimOneAsync(
                new ClaimOptions(namespaceId, workerId, MaxBatch: 1, ExcludedDefinitionIds: _context.UnsupportedDefinitionIdsSnapshot),
                _leaseTtlSeconds,
                null,
                ct
            );
        if (claim.Jobs.Count == 0)
        {
            _metrics?.RecordClaim(namespaceName, "nothing-claimed");
            return RunOnceStatus.NothingClaimed;
        }

        _metrics?.RecordClaim(namespaceName, "claimed");

        // No lease deadline: the CLI's jobs debug run pumps the heartbeat around a handler a debugger may hold
        // paused past it. The heartbeat still cancels the attempt once its row is gone.
        var entry = new InFlightJob(claim.Jobs[0], leaseRunway: null, ct);
        _registry.TryAdd(entry);
        return await RunInlineAsync(entry, ct);
    }

    /// <summary>Executes and finalizes one entry on the caller's task, for the paths that hold no permit.</summary>
    public async Task<RunOnceStatus> RunInlineAsync(InFlightJob entry, CancellationToken ct) =>
        await ExecuteAsync(entry, ct) ? await finalizer.FinalizeAsync(entry, ct) : RunOnceStatus.NothingClaimed;

    /// <summary>
    /// One of the worker's executor loops: runs each entry the claimer hands over, one at a time, until the claimer
    /// completes the channel and it is empty, and hands each completion to the finalizers before returning the
    /// entry's permit. A fault is logged per entry, so one bad job never ends the loop.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken hostCt)
    {
        try
        {
            await foreach (var entry in _registry.Claimed.Reader.ReadAllAsync(hostCt))
            {
                try
                {
                    if (await ExecuteAsync(entry, hostCt))
                    {
                        await _registry.Completed.Writer.WriteAsync(entry, hostCt);
                        _registry.ReleasePermit(entry);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !hostCt.IsCancellationRequested)
                {
                    _log.LogError(ex, "WorkerRuntime: executor faulted on job {JobId}.", entry.Claim.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (hostCt.IsCancellationRequested)
        {
            // A hard stop: entries still in the channel lapse to recovery.
        }
    }

    /// <summary>
    /// Runs one held entry up to its completion: true once the entry carries its completion and is Finalizing, for the
    /// finalizer to write; false after removing an entry that completes nothing here, because its row is gone, its
    /// start was refused, or the worker stopped mid-handler, which leaves the row Executing for recovery. A claim
    /// whose definition this deployment carries no descriptor for completes as a hand-back, because claims are by
    /// namespace and a worker is therefore free to claim work it cannot run.
    /// </summary>
    public async Task<bool> ExecuteAsync(InFlightJob entry, CancellationToken hostCt)
    {
        var job = entry.Claim;
        try
        {
            var namespaceName = _context.WorkerRegistration!.NamespaceName;
            var (namespaceId, workerId) = _context.ResolveWorker(namespaceName);
            // The heartbeat found the row gone, or the lease lapsed while the claim waited for an executor: the
            // row is no longer this worker's to run.
            if (entry.AttemptToken.IsCancellationRequested || !entry.Started && !await StartAsync(entry, workerId))
            {
                return false;
            }

            entry.State = InFlightJobState.Executing;
            if (!_context.DescriptorByDefinitionId.TryGetValue(job.DefinitionId, out var descriptor))
            {
                // One warning per definition, not per bounce: the exclusion makes the first bounce the whole
                // story, and the rest of an already-claimed batch would otherwise repeat it once per row. The job
                // name is not known - resolving it is what the missing descriptor would have done - so the ref is
                // what an operator takes to `jobs explain`.
                _log.Log(
                    _context.ExcludeDefinition(job.DefinitionId) ? LogLevel.Warning : LogLevel.Debug,
                    "WorkerRuntime: ({Namespace}) job {JobId} ({Detail}) claimed with no handler in this deployment; returned to Ready in {DurationMs}ms; excluded from this worker's claims until restart.",
                    namespaceName,
                    job.JobId,
                    $"ref {job.JobRef}, definition_id {job.DefinitionId}",
                    _handBackDelayMs
                );
                return Finalizing(
                    entry,
                    HandBack(
                        $"No handler for definition_id={job.DefinitionId} in this deployment; returned to Ready for a worker that has one."
                    )
                );
            }

            // One scope per attempt carrying the job identity. Opened on the runtime logger, which shares
            // the factory's external scope provider, so the handler's own ILogger<T> lines inherit these
            // fields too. Covers the lock and the attempt.
            using var logScope = _log.BeginScope(
                JobLogScope.For(job.JobId, descriptor.JobName, namespaceName, job.ExecutionNumber, workerId, job.CorrelationKey)
            );

            // Clamped to CancelAfter's ceiling regardless of where the value came from (descriptor or
            // operator override): an over-limit value would throw right here, after the claim, and every
            // reclaim would walk the job straight back into the same throw.
            var timeoutSeconds = Math.Min(
                descriptor.ExecutionTimeoutSeconds ?? JobDefinitionRegistration.DefaultExecutionTimeoutSeconds,
                JobDefinitionRegistration.MaxExecutionTimeoutSeconds
            );
            if (timeoutSeconds > 0)
            {
                entry.ArmTimeout(TimeSpan.FromSeconds(timeoutSeconds));
            }

            await using var attemptScope = _rootServices.CreateAsyncScope();
            var attemptServices = attemptScope.ServiceProvider;
            AttemptSetup setup;
            try
            {
                setup = await _contexts.ReadSetupAsync(job, descriptor, hostCt);
            }
            catch (Exception ex) when (!hostCt.IsCancellationRequested)
            {
                _log.LogError(
                    ex,
                    "WorkerRuntime: attempt setup failed before the handler ran for job {JobId}; returning it to Ready.",
                    job.JobId
                );
                return Finalizing(
                    entry,
                    HandBack($"Attempt setup failed before the handler ran ({ex.GetType().Name}); returned to Ready.")
                );
            }
            var jobContext = _contexts.Create(job, descriptor, namespaceName, namespaceId, workerId, setup, entry, attemptServices);

            // Publish the context on the attempt scope so DI-resolved handlers (e.g. MediatR) inject it.
            // The handler resolves from this same scope, so the scoped accessor carries the set value.
            attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;

            var completion = await _jobExecution.RunAsync(attemptServices, descriptor, job, jobContext, setup.Fire, hostCt);
            if (completion is null)
            {
                return false;
            }

            entry.Descriptor = descriptor;
            entry.Context = jobContext;
            entry.Fire = setup.Fire;
            return Finalizing(entry, completion);
        }
        finally
        {
            if (entry.State != InFlightJobState.Finalizing)
            {
                _registry.Remove(entry);
            }
        }
    }

    /// <summary>
    /// Moves a Dispatched claim to Executing, repeated until it answers or the entry's attempt token stops it, since a
    /// start abandoned after the claim leaves the row to wait out its lease; a start that committed and lost its
    /// answer is answered Started again.
    /// </summary>
    private async Task<bool> StartAsync(InFlightJob entry, int workerId)
    {
        var job = entry.Claim;
        var start = await JobFinalizer.RepeatAsync(
            token => _execution.StartExecutionAsync(job.JobId, workerId, job.ExecutionNumber, _leaseTtlSeconds, token),
            _log,
            job.JobId,
            entry.AttemptToken,
            _metrics
        );
        if (start.Action == StartExecutionAction.Started)
        {
            return true;
        }

        // The claim was lost before execution began: reclaimed by recovery, reassigned, or moved out of
        // Dispatched by an operator control verb between claim and start. The start mutated nothing; clean
        // skip, and the row is re-claimed on the next tick.
        _log.LogInformation(
            "WorkerRuntime: lost claim on job {JobId} ({Detail}) before start: ({Outcome}); skipping.",
            job.JobId,
            $"execution number {job.ExecutionNumber}",
            start.Action.ToString()
        );
        return false;
    }

    // A claim this worker will not run goes back to Ready budget-neutral, through the finalizer, which writes it as a
    // hand-back because the entry carries no descriptor.
    private static ExecutionCompletion HandBack(string reasonMessage) =>
        new(CompletionKind.Rescheduled) { Reason = EventReasonCode.Unclassified, ReasonMessage = reasonMessage };

    private static bool Finalizing(InFlightJob entry, ExecutionCompletion completion)
    {
        entry.Completion = completion;
        entry.State = InFlightJobState.Finalizing;
        return true;
    }
}
