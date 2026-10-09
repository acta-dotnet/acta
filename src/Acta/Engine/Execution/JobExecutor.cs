using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Executes one already-claimed job: resolves the descriptor, opens the per-attempt DI scope, plans a recurring slot
/// fire, builds the <see cref="RuntimeJobContext"/> and publishes it on the scope, then hands the
/// start-admit-invoke-complete lifecycle to <see cref="JobExecution"/>, whose admission takes the concurrency-key
/// slot after the start CAS and bounces a loser back to Ready. Claiming jobs and dispatching them to executors is the
/// worker loop's job. A claim this deployment carries no handler for is returned to Ready instead of executed.
/// </summary>
internal sealed class JobExecutor(
    Locks locks,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    IServiceProvider rootServices,
    IOptions<JobsOptions> options,
    WorkerContext context,
    InFlightJobRegistry registry,
    JobExecution jobExecution,
    ILogger? log = null,
    ActaMetrics? metrics = null
)
{
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly WorkerContext _context = context;
    private readonly InFlightJobRegistry _registry = registry;
    private readonly JobExecution _jobExecution = jobExecution;
    private readonly Execution _execution = rootServices.GetRequiredService<Execution>();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ActaMetrics? _metrics = metrics;
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

    // Re-arm delay for a claim this deployment cannot run, in whole seconds (the store verb's unit).
    // SafetyPollInterval is the honest unit for it: the option is documented as the bound on how long
    // a Ready row waits to be found by a process this one shares no wakeup transport with, and a
    // process that carries the missing handler is exactly who has to pick this job up. Validation
    // keeps it at or above one second, so the delay never rounds to zero.
    private readonly int _unsupportedClaimDelaySeconds = (int)Math.Ceiling(options.Value.SafetyPollInterval.TotalSeconds);

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the concurrency-key lock). The deterministic single-shot primitive: the
    /// production loop drives it from N executor loops; tests drive it directly.
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
        return await ExecuteClaimedJobAsync(entry, namespaceName, namespaceId, workerId, alreadyStarted: false, ct);
    }

    /// <summary>
    /// Runs one registered claim through the attempt lifecycle, then removes it from the registry. A claim whose
    /// definition this deployment carries no descriptor for is returned to Ready instead (see
    /// <see cref="ReleaseUnsupportedClaimAsync"/>), because claims are by namespace and a worker is
    /// therefore free to claim work it cannot run.
    /// </summary>
    public async Task<RunOnceStatus> ExecuteClaimedJobAsync(
        InFlightJob entry,
        string namespaceName,
        int namespaceId,
        int workerId,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        var job = entry.Claim;
        try
        {
            // The heartbeat found the row gone, or the lease lapsed while the claim waited for an executor: the
            // row is no longer this worker's to run.
            if (entry.AttemptToken.IsCancellationRequested)
            {
                return RunOnceStatus.NothingClaimed;
            }
            entry.State = InFlightJobState.Executing;
            if (!_context.DescriptorByDefinitionId.TryGetValue(job.DefinitionId, out var descriptor))
            {
                var firstBounce = _context.ExcludeDefinition(job.DefinitionId);
                return await ReleaseUnsupportedClaimAsync(job, namespaceName, workerId, alreadyStarted, firstBounce, ct);
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
            // The claim is this worker's from here until something settles it, so anything that fails
            // before the handler runs hands the claim back rather than leaving it leased and unowned.
            AttemptSetup setup;
            try
            {
                setup = await _contexts.ReadSetupAsync(job, descriptor, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogError(
                    ex,
                    "WorkerRuntime: attempt setup failed before the handler ran for job {JobId}; returning it to Ready.",
                    job.JobId
                );
                await ReleaseClaimAsync(
                    job,
                    workerId,
                    alreadyStarted,
                    $"Attempt setup failed before the handler ran ({ex.GetType().Name}); returned to Ready.",
                    ct
                );
                throw;
            }
            var jobContext = _contexts.Create(job, descriptor, namespaceName, namespaceId, workerId, setup, entry, attemptServices);

            // Publish the context on the attempt scope so DI-resolved handlers (e.g. MediatR) inject it.
            // The handler resolves from this same scope, so the scoped accessor carries the set value.
            attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;

            return await _jobExecution.RunAsync(attemptServices, descriptor, job, jobContext, workerId, setup.Fire, alreadyStarted, ct);
        }
        finally
        {
            _registry.Remove(entry);
        }
    }

    /// <summary>
    /// Returns a claim this deployment has no handler for to Ready: budget-neutral, lease cleared, due again after
    /// <see cref="JobsOptions.SafetyPollInterval"/>, so a worker that carries the definition can take it (claims are
    /// selected by namespace, so every rolling deploy hands some worker a job it cannot run). The caller has excluded
    /// the definition from this worker's claims, so each costs one bounce rather than one per job per interval. The
    /// claim never becomes an attempt: no scope, no handler. Throwing instead would leave the row leased
    /// until its lease lapsed and recovery took it.
    /// </summary>
    private async Task<RunOnceStatus> ReleaseUnsupportedClaimAsync(
        ClaimedJob job,
        string namespaceName,
        int workerId,
        bool alreadyStarted,
        bool firstBounce,
        CancellationToken ct
    )
    {
        // One warning per definition, not per bounce: the exclusion makes the first bounce the whole
        // story, and the rest of an already-claimed batch would otherwise repeat it once per row.
        // This is the only place the pair (namespace, definition id) is known. The job name is not -
        // resolving it is what the missing descriptor would have done - so the ref is what an operator
        // takes to `jobs explain`.
        _log.Log(
            firstBounce ? LogLevel.Warning : LogLevel.Debug,
            "WorkerRuntime: ({Namespace}) job {JobId} ({Detail}) claimed with no handler in this deployment; returned to Ready in {DurationMs}ms; excluded from this worker's claims until restart.",
            namespaceName,
            job.JobId,
            $"ref {job.JobRef}, definition_id {job.DefinitionId}",
            _unsupportedClaimDelaySeconds * 1000
        );

        // No wakeup publish, deliberately: every claim loop re-polls within SafetyPollInterval, which
        // is the delay itself, and waking this namespace would wake this worker's own loops first -
        // the loops that have just excluded this definition and so are the least able to use the wake.
        return await ReleaseClaimAsync(
            job,
            workerId,
            alreadyStarted,
            $"No handler for definition_id={job.DefinitionId} in this deployment; returned to Ready for a worker that has one.",
            ct
        );
    }

    /// <summary>
    /// Returns a claim this worker will not run to Ready, budget-neutral, lease cleared, due again after
    /// <see cref="JobsOptions.SafetyPollInterval"/>. Both writes are repeated until they land, because a claim nothing
    /// settles waits out its lease before recovery returns it.
    /// </summary>
    private async Task<RunOnceStatus> ReleaseClaimAsync(
        ClaimedJob job,
        int workerId,
        bool alreadyStarted,
        string reasonMessage,
        CancellationToken ct
    )
    {
        // complete_execution's CAS matches an Executing row only, so the claim is walked through the
        // start first even though nothing will run. The combined claim loop already started the
        // execution in the claim itself.
        if (!alreadyStarted)
        {
            var (start, _) = await CompletionWrite.RetryAsync(
                token => _execution.StartExecutionAsync(job.JobId, workerId, job.ExecutionNumber, _leaseTtlSeconds, token),
                _log,
                job.JobId,
                ct,
                _metrics
            );
            if (start.Action != StartExecutionAction.Started)
            {
                // Reclaimed on lease expiry, reassigned, or moved out of Dispatched by a control verb:
                // the row is no longer this worker's to release.
                _log.LogInformation(
                    "WorkerRuntime: lost claim on job {JobId} ({Detail}) before releasing it: ({Outcome}); skipping.",
                    job.JobId,
                    $"definition_id {job.DefinitionId}",
                    start.Action.ToString()
                );
                return RunOnceStatus.NothingClaimed;
            }
        }

        // Unclassified rather than a borrowed code: no catalog reason describes a worker declining a
        // claim, and the two candidates would both misreport it - JobAttemptAborted is published as a
        // mid-flight abort retried under the failure budget, JobDefinitionRetired as a catalog
        // retirement that cancels. Unclassified is the writer saying the story is in the message, and
        // it stays out of both the alertable set and the Failures audit level, which a bounce belongs
        // outside of: nothing failed.
        var request = new CompleteExecutionCommand(
            job.JobId,
            workerId,
            job.ExecutionNumber,
            CompletionKind.Rescheduled,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventReasonCode.Unclassified,
            reasonMessage.Truncate(ActaTextLimits.ReasonMessage),
            DurationMs: 0
        )
        {
            RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
            RescheduleDelaySeconds = _unsupportedClaimDelaySeconds,
        };
        var (complete, _) = await CompletionWrite.RetryAsync(
            token => _execution.CompleteExecutionAsync(request, token),
            _log,
            job.JobId,
            ct,
            _metrics
        );
        return complete.Action == CompleteExecutionAction.Completed ? RunOnceStatus.Rearmed : RunOnceStatus.NothingClaimed;
    }
}
