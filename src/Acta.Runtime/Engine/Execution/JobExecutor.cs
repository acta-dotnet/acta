using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime;

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
    JobExecution jobExecution,
    ILogger? log = null,
    JobMetrics? metrics = null
)
{
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly WorkerContext _context = context;
    private readonly JobExecution _jobExecution = jobExecution;
    private readonly Execution _execution = rootServices.GetRequiredService<Execution>();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly JobMetrics? _metrics = metrics;
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
            ? await _execution.ClaimOneAsync(new ClaimRequest(namespaceId, workerId, MaxBatch: 1), _leaseTtlSeconds, id, ct)
            : await _execution.ClaimOneAsync(
                new ClaimRequest(namespaceId, workerId, MaxBatch: 1, ExcludedDefinitionIds: _context.UnsupportedDefinitionIdsSnapshot),
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

        return await ExecuteClaimedJobAsync(claim.Jobs[0], namespaceName, namespaceId, workerId, alreadyStarted: false, ct);
    }

    /// <summary>
    /// Runs one claimed job through the attempt lifecycle. A claim whose definition this deployment
    /// carries no descriptor for is returned to Ready instead (see
    /// <see cref="ReleaseUnsupportedClaimAsync"/>), because claims are by namespace and a worker is
    /// therefore free to claim work it cannot run.
    /// </summary>
    public async Task<RunOnceStatus> ExecuteClaimedJobAsync(
        ClaimedJob job,
        string namespaceName,
        int namespaceId,
        int workerId,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        // A claim whose answer arrived late enough for the heartbeat to be releasing the row is left
        // to that release: it reads as this worker's row all the way through, so only the in-process
        // owner entry tells the two actors apart.
        var execution = (job.JobId, job.ExecutionNumber);
        if (!_context.AttemptOwners.TryAdd(execution, 0))
        {
            _log.LogInformation(
                "WorkerRuntime: ({Namespace}) job {JobId} is being released as an orphaned claim; the late claim answer is skipped.",
                namespaceName,
                job.JobId
            );
            return RunOnceStatus.NothingClaimed;
        }

        try
        {
            return await ExecuteOwnedClaimAsync(job, namespaceName, namespaceId, workerId, alreadyStarted, ct);
        }
        finally
        {
            _context.AttemptOwners.TryRemove(execution, out _);
        }
    }

    private async Task<RunOnceStatus> ExecuteOwnedClaimAsync(
        ClaimedJob job,
        string namespaceName,
        int namespaceId,
        int workerId,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        if (!_context.DescriptorByDefinitionId.TryGetValue(job.DefinitionId, out var descriptor))
        {
            var firstBounce = _context.ExcludeDefinition(job.DefinitionId);
            return await ReleaseUnsupportedClaimAsync(job, namespaceName, workerId, alreadyStarted, firstBounce, ct);
        }

        // One scope per attempt carrying the job identity. Opened on the runtime logger, which shares
        // the factory's external scope provider, so the handler's own ILogger<T> lines inherit these
        // fields too. Covers the lock, the attempt, and the finally.
        using var logScope = _log.BeginScope(
            JobLogScope.For(job.JobId, descriptor.JobName, namespaceName, job.ExecutionNumber, workerId, job.CorrelationKey)
        );

        var (attempt, attemptCts, timeoutCts) = RegisterAttempt(job, descriptor, ct);
        try
        {
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
            var jobContext = _contexts.Create(
                job,
                descriptor,
                namespaceName,
                namespaceId,
                workerId,
                setup,
                attempt,
                attemptServices,
                attemptCts.Token
            );

            // Publish the context on the attempt scope so DI-resolved handlers (e.g. MediatR) inject it.
            // The handler resolves from this same scope, so the scoped accessor carries the set value.
            attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;

            return await _jobExecution.RunAsync(attemptServices, descriptor, job, jobContext, workerId, setup.Fire, alreadyStarted, ct);
        }
        finally
        {
            // Identity-conditional: a reclaimed job can be re-dispatched in-process while this stale
            // attempt is still unwinding, so removing by key alone would untrack the replacement.
            _context.RunningAttempts.TryRemove(new KeyValuePair<long, RunningAttempt>(job.JobId, attempt));
            attemptCts.Dispose();
            timeoutCts.Dispose();
        }
    }

    /// <summary>
    /// Links a per-attempt cancellation source off the worker token AND a dedicated timeout source; the
    /// heartbeat cancels it when an external cancel or lease steal drops this job from the worker's
    /// lease set, and the timeout source cancels it when the per-attempt wall-clock cap elapses. The
    /// separate timeout source lets the execution tell a timeout from an external cancel. Registering the
    /// attempt also lets the heartbeat extend every lock it holds.
    /// </summary>
    private (RunningAttempt Attempt, CancellationTokenSource AttemptCts, CancellationTokenSource TimeoutCts) RegisterAttempt(
        ClaimedJob job,
        JobDescriptor descriptor,
        CancellationToken ct
    )
    {
        var timeoutCts = new CancellationTokenSource();
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var attempt = new RunningAttempt(attemptCts, timeoutCts: timeoutCts)
        {
            ExecutionNumber = job.ExecutionNumber,
            // Seed the monotonic job-lease deadline. The claim stamped the DB lease at most `now` (dispatch
            // runs after the claim), so now + LeaseTtl is a slight over-estimate of it; the first worker
            // heartbeat re-seeds it conservatively from that renewal's request-start, and the watchdog's
            // unwind margin absorbs the seed's slack in the meantime.
            JobLeaseGoodUntil = Stopwatch.GetTimestamp() + (long)(_leaseTtlSeconds * (double)Stopwatch.Frequency),
        };
        // Clamped to CancelAfter's ceiling regardless of where the value came from (descriptor or
        // operator override): an over-limit value would throw right here - after the claim, before the
        // cleanup try - and every reclaim would walk the job straight back into the same throw.
        var timeoutSeconds = Math.Min(
            descriptor.ExecutionTimeoutSeconds ?? JobDefinitionRegistration.DefaultExecutionTimeoutSeconds,
            JobDefinitionRegistration.MaxExecutionTimeoutSeconds
        );
        if (timeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        }
        _context.RunningAttempts[job.JobId] = attempt;
        return (attempt, attemptCts, timeoutCts);
    }

    /// <summary>
    /// Returns a claim this deployment has no handler for to Ready: budget-neutral, lease cleared, due again after
    /// <see cref="JobsOptions.SafetyPollInterval"/>, so a worker that carries the definition can take it (claims are
    /// selected by namespace, so every rolling deploy hands some worker a job it cannot run). The caller has excluded
    /// the definition from this worker's claims, so each costs one bounce rather than one per job per interval. The
    /// claim was never an attempt: nothing registered, no scope, no handler. Throwing instead would strand the row, since
    /// the heartbeat renews every leased row from database state alone and <c>sys.recovery</c> never reclaims it.
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
    /// Returns to Ready the rows the heartbeat found leased by this process with no attempt behind them: claims whose
    /// answer was lost after the store committed them. Each row is read again first, and only one still leased by one
    /// of this process's workers, still Dispatched or Executing, and still unaccounted for is released, through the
    /// same start-then-reschedule walk an unsupported claim takes, so the ledger shows an attempt that ran nothing.
    /// </summary>
    public async Task ReleaseOrphanedClaimsAsync(IReadOnlyList<long> jobIds, string reason, CancellationToken ct)
    {
        var getJob = _rootServices.GetRequiredService<GetJobHandler>();
        var workerIds = _context.WorkerIdByNamespace.Values.ToHashSet();
        foreach (var jobId in jobIds)
        {
            await ReleaseOrphanedClaimAsync(jobId, getJob, workerIds, reason, ct);
        }
    }

    private async Task ReleaseOrphanedClaimAsync(
        long jobId,
        GetJobHandler getJob,
        HashSet<int> workerIds,
        string reason,
        CancellationToken ct
    )
    {
        if (_context.BufferedClaims.ContainsKey(jobId))
        {
            return;
        }

        var (row, _) = await CompletionWrite.RetryAsync(async token => await getJob.ReadAsync(jobId, token), _log, jobId, ct, _metrics);
        // The ownership check repeats after the read: the claim loop registers a row the instant its
        // claim answers, so a row unaccounted for at the first check may be buffered by now. Only an
        // attempt on the row's own execution accounts for it: an older one still unwinding does not.
        if (
            row is null
            || row.LeasedByWorkerId is not { } workerId
            || !workerIds.Contains(workerId)
            || row.Status is not (JobStatusCode.Dispatched or JobStatusCode.Executing)
            || (_context.RunningAttempts.TryGetValue(jobId, out var running) && running.ExecutionNumber == row.ExecutionNumber)
            || _context.BufferedClaims.ContainsKey(jobId)
        )
        {
            return;
        }

        // The owner entry for this execution is held from here until the reschedule lands, so a claim
        // answer that arrives meanwhile finds the execution taken and skips it.
        var execution = (jobId, row.ExecutionNumber);
        if (!_context.AttemptOwners.TryAdd(execution, 0))
        {
            return;
        }

        try
        {
            await ReleaseOwnedClaimAsync(row, jobId, workerId, getJob, reason, ct);
        }
        finally
        {
            _context.AttemptOwners.TryRemove(execution, out _);
        }
    }

    private async Task ReleaseOwnedClaimAsync(
        JobDetail row,
        long jobId,
        int workerId,
        GetJobHandler getJob,
        string reason,
        CancellationToken ct
    )
    {
        if (row.Status == JobStatusCode.Dispatched)
        {
            var start = await JobExecution.StartReconciledAsync(
                _execution,
                getJob,
                jobId,
                row.ExecutionNumber,
                row.Version,
                workerId,
                _leaseTtlSeconds,
                _log,
                _metrics,
                ct
            );
            if (start != StartExecutionAction.Started)
            {
                _log.LogInformation(
                    "WorkerRuntime: lost claim on job {JobId} ({Detail}) before releasing it: ({Outcome}); skipping.",
                    jobId,
                    $"execution number {row.ExecutionNumber}",
                    start.ToString()
                );
                return;
            }
        }

        var request = new CompleteExecutionCommand(
            jobId,
            workerId,
            row.ExecutionNumber,
            CompletionKind.Rescheduled,
            0,
            ReadOnlyMemory<byte>.Empty,
            EventReasonCode.Unclassified,
            reason.Truncate(ActaTextLimits.ReasonMessage),
            DurationMs: 0
        )
        {
            RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
            RescheduleDelaySeconds = _unsupportedClaimDelaySeconds,
        };
        var (complete, _) = await CompletionWrite.RetryAsync(
            token => _execution.CompleteExecutionAsync(request, token),
            _log,
            jobId,
            ct,
            _metrics
        );
        _log.LogWarning(
            "WorkerRuntime: released job {JobId} ({Detail}): ({Outcome}).",
            jobId,
            $"execution number {row.ExecutionNumber}; {reason}",
            complete.Action.ToString()
        );
    }

    /// <summary>
    /// Returns a claim this worker will not run to Ready, budget-neutral, lease cleared, due again after
    /// <see cref="JobsOptions.SafetyPollInterval"/>. Both writes are repeated until they land, because a claim nothing
    /// settles stays leased for as long as this worker lives.
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
            var start = await JobExecution.StartReconciledAsync(
                _execution,
                _rootServices.GetRequiredService<GetJobHandler>(),
                job,
                workerId,
                _leaseTtlSeconds,
                _log,
                _metrics,
                ct
            );
            if (start != StartExecutionAction.Started)
            {
                // Reclaimed on lease expiry, reassigned, or moved out of Dispatched by a control verb:
                // the row is no longer this worker's to release.
                _log.LogInformation(
                    "WorkerRuntime: lost claim on job {JobId} ({Detail}) before releasing it: ({Outcome}); skipping.",
                    job.JobId,
                    $"definition_id {job.DefinitionId}",
                    start.ToString()
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
