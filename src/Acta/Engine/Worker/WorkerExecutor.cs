using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Acta;

/// <summary>
/// Executes one already-claimed job up to its completion: resolves the descriptor, opens the per-attempt DI scope,
/// reads what the attempt needs, builds the <see cref="RuntimeJobContext"/> and publishes it on the scope, then runs
/// <see cref="ExecutionAdmission"/>, which completes a refused attempt, and <see cref="HandlerInvocation"/>, which
/// completes an admitted one. Its loops take what <see cref="WorkerClaimer"/> claimed and hand each completion to
/// <see cref="WorkerFinalizer"/>, so an executor is free as soon as the completion is built. A claim this deployment
/// carries no handler for completes as a hand-back to Ready.
/// </summary>
internal sealed class WorkerExecutor(
    LocksOperations locks,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    IServiceProvider rootServices,
    JobsOptions options,
    JobBehaviorPipeline pipeline,
    WorkerContext context,
    InFlightJobRegistry registry,
    WorkerFinalizer finalizer,
    ILogger? log = null,
    ActaMetrics? metrics = null
)
{
    private readonly JobsOptions _options = options;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly ExecutionOperations _execution = rootServices.GetRequiredService<ExecutionOperations>();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ExecutionAdmission _admission = new(log ?? NullLogger.Instance, options.ConcurrencyKeyBounceDelaySeconds);
    private readonly HandlerInvocation _invocation = new(serializers, pipeline, options.MaxInlinePayloadBytes, log ?? NullLogger.Instance);

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the concurrency-key lock). The deterministic single-shot primitive, holding no
    /// executor permit.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, explicitJobId: null, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (through <c>claim_job</c>), for callers that already know which job
    /// to run. Still a single tick: the claim uses READPAST, so a transiently-locked row yields
    /// <see cref="RunOnceStatus.NothingClaimed"/>, and the caller decides whether to tick again.
    /// </summary>
    public Task<RunOnceStatus> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, jobId, ct);

    private async Task<RunOnceStatus> RunOnceCoreAsync(string namespaceName, long? explicitJobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        var (namespaceId, workerId) = context.ResolveWorker(namespaceName);

        // Only the scanning claim carries the exclusion: a caller naming a job id asked for that row,
        // and gets the bounce rather than an empty claim it cannot tell from a locked-away one.
        var claim = explicitJobId is { } id
            ? await _execution.ClaimJobAsync(new ClaimOptions(namespaceId, workerId, MaxBatch: 1), _options.LeaseTtlSeconds, id, ct)
            : await _execution.ClaimJobAsync(
                new ClaimOptions(namespaceId, workerId, MaxBatch: 1, ExcludedDefinitionIds: context.UnsupportedDefinitionIdsSnapshot),
                _options.LeaseTtlSeconds,
                null,
                ct
            );
        if (claim.Jobs.Count == 0)
        {
            metrics?.RecordClaim(namespaceName, "nothing-claimed");
            return RunOnceStatus.NothingClaimed;
        }

        metrics?.RecordClaim(namespaceName, "claimed");

        // No lease deadline: the CLI's jobs debug run pumps the heartbeat around a handler a debugger may hold paused
        // past it. The heartbeat still cancels the attempt once its row is gone.
        var entry = new InFlightJob(claim.Jobs[0], leaseRunway: null, ct);
        registry.TryAdd(entry);
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
            await foreach (var entry in registry.Claimed.Reader.ReadAllAsync(hostCt))
            {
                try
                {
                    if (await ExecuteAsync(entry, hostCt))
                    {
                        await registry.Completed.Writer.WriteAsync(entry, hostCt);
                        registry.ReleasePermit(entry);
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
            var namespaceName = context.WorkerRegistration!.NamespaceName;
            var (namespaceId, workerId) = context.ResolveWorker(namespaceName);
            // The heartbeat found the row gone, or the lease lapsed while the claim waited for an executor: the
            // row is no longer this worker's to run.
            if (entry.AttemptToken.IsCancellationRequested)
            {
                return false;
            }

            entry.State = InFlightJobState.Executing;
            if (!context.DescriptorByDefinitionId.TryGetValue(job.DefinitionId, out var descriptor))
            {
                // One warning per definition, not per bounce: the exclusion makes the first bounce the whole
                // story, and the rest of an already-claimed batch would otherwise repeat it once per row. The job
                // name is not known - resolving it is what the missing descriptor would have done - so the ref is
                // what an operator takes to `jobs explain`.
                _log.Log(
                    context.ExcludeDefinition(job.DefinitionId) ? LogLevel.Warning : LogLevel.Debug,
                    "WorkerRuntime: ({Namespace}) job {JobId} ({Detail}) claimed with no handler in this deployment; returned to Ready in {DurationMs}ms; excluded from this worker's claims until restart.",
                    namespaceName,
                    job.JobId,
                    $"ref {job.JobRef}, definition_id {job.DefinitionId}",
                    (long)_options.SafetyPollInterval.TotalMilliseconds
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

            var attemptScope = _rootServices.CreateAsyncScope();
            try
            {
                (RuntimeJobContext Context, RecurringFirePlan? Fire) attempt;
                try
                {
                    attempt = await PrepareAsync(
                        entry,
                        descriptor,
                        namespaceName,
                        namespaceId,
                        workerId,
                        attemptScope.ServiceProvider,
                        hostCt
                    );
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

                var (jobContext, fire) = attempt;
                ExecutionCompletion? completion;
                try
                {
                    // A fire that only ends timed pauses with nothing due takes no slot, no turn, and no handler call;
                    // it completes as a re-arm that applies the advances (ScheduleWalker.PlanFire).
                    completion = fire is { NothingDue: true }
                        ? new ExecutionCompletion(CompletionKind.Rescheduled)
                        {
                            ReasonMessage = "A timed schedule pause ended with no occurrence due; the handler did not run.",
                        }
                        : await _admission.AdmitAsync(descriptor, job, jobContext, hostCt)
                            ?? await _invocation.InvokeAsync(attemptScope.ServiceProvider, descriptor, job, jobContext, hostCt);
                }
                finally
                {
                    // Mutual exclusion covers the handler, not the completion write: the slot admission took goes back on
                    // every exit, a refusal, the handler's end, or worker shutdown. Release is best-effort, non-throwing, and
                    // a no-op when no slot was taken; a failed release self-heals via the lock's TTL.
                    await jobContext.ReleaseConcurrencySlotAsync(CancellationToken.None);
                }

                if (completion is null)
                {
                    return false;
                }

                entry.Descriptor = descriptor;
                entry.Context = jobContext;
                entry.Fire = fire;
                return Finalizing(entry, completion);
            }
            finally
            {
                await DisposeAttemptScopeAsync(attemptScope, job.JobId);
            }
        }
        finally
        {
            if (entry.State != InFlightJobState.Finalizing)
            {
                registry.Remove(entry);
            }
        }
    }

    /// <summary>
    /// What an attempt needs before its handler runs: the recurring fire plan and the tenant name, each read repeated
    /// like the writes on the host token, then the context, published on the attempt scope so DI-resolved handlers
    /// (e.g. MediatR) inject it from the same scope the handler resolves from.
    /// </summary>
    private async Task<(RuntimeJobContext Context, RecurringFirePlan? Fire)> PrepareAsync(
        InFlightJob entry,
        JobDescriptor descriptor,
        string namespaceName,
        int namespaceId,
        int workerId,
        IServiceProvider attemptServices,
        CancellationToken ct
    )
    {
        var job = entry.Claim;
        var fire = await PlanFireAsync(job, descriptor, ct);
        var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);

        // Resolve the external tenant name off the process-lifetime cache (one point read per
        // distinct tenant); the claim projection itself stays join-free.
        var tenantName = job.TenantId is { } jobTenantId
            ? await WorkerFinalizer.RepeatAsync(
                async token =>
                    (await _rootServices.GetRequiredService<TenantsOperations>().GetTenantNameAsync(jobTenantId, token))?.TenantName,
                _log,
                job.JobId,
                ct,
                metrics
            )
            : null;

        // A recurring slot never carries the whole-job deadline: it anchors to job creation
        // and the slot row lives forever, so the Strict paths would cancel the slot
        // terminally and the advisory surface (ctx.IsOverdue, ctx.TimeUntilDeadline) would
        // report permanently overdue to every occurrence. Worker init rejects the
        // Deadline + [JobSchedule] combination; a null here covers slots that predate the
        // rule or gained a deadline through an override, in the one place every consumer
        // reads from.
        DateTime? deadlineAtUtc =
            fire is null && descriptor.DeadlineSeconds is { } deadlineSecs && deadlineSecs > 0
                ? job.CreatedAtUtc.AddSeconds(deadlineSecs)
                : null;
        var jobContext = new RuntimeJobContext(
            job,
            descriptor.JobName,
            namespaceName,
            namespaceId,
            _options.LeaseTtlSeconds,
            _rootServices.GetRequiredService<JobsOperations>(),
            _rootServices.GetRequiredService<SignalsOperations>(),
            _rootServices.GetRequiredService<AlertsOperations>(),
            _execution,
            _serializers,
            locks,
            fire?.TriggeringScheduleNames ?? [],
            deadlineAtUtc,
            entry.AttemptToken,
            _options.MaxInlinePayloadBytes,
            entry,
            new StepRetryDefaults(
                descriptor.MaxAttempts,
                DurationSyntax.ToWholeSeconds(backoff.InitialDelay, nameof(backoff)),
                DurationSyntax.ToWholeSeconds(backoff.MaxDelay, nameof(backoff)),
                (decimal)backoff.Multiplier,
                (decimal)backoff.Jitter
            ),
            _log,
            metrics,
            attemptServices.GetService<IJobs>(),
            tenantName,
            workerId,
            clock
        );
        attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;
        return (jobContext, fire);
    }

    /// <summary>
    /// Recurring slot fire: reads live schedules once and plans the due set + cursor advances at a single
    /// captured nowUtc. The due names are visible to the handler; the advances + slot MIN apply at
    /// completion. Null for a one-shot job, which keeps the unchanged one-shot path.
    /// </summary>
    private async Task<RecurringFirePlan?> PlanFireAsync(ClaimedJob job, JobDescriptor descriptor, CancellationToken ct)
    {
        var isRecurring = context.RecurringSlotJobIds.ContainsKey(job.JobId);
        if (!isRecurring && !string.Equals(job.DeduplicationKey, descriptor.JobName, StringComparison.Ordinal))
        {
            return null;
        }

        var nowUtc = await WorkerFinalizer.RepeatAsync(async token => await clock.GetUtcNowAsync(token), _log, job.JobId, ct, metrics);
        var schedules = _rootServices.GetRequiredService<SchedulesOperations>();
        var live = await WorkerFinalizer.RepeatAsync(
            async token => (await schedules.GetLiveSchedulesAsync(job.JobId, token)).Schedules,
            _log,
            job.JobId,
            ct,
            metrics
        );
        // A slot another runtime registered after this one started is not in the startup set. Its
        // deduplication key is its job name, which a one-off may share, so live schedules decide.
        if (!isRecurring && live.Count > 0)
        {
            isRecurring = true;
            context.RecurringSlotJobIds.TryAdd(job.JobId, 0);
        }
        return isRecurring ? ScheduleWalker.PlanFire(live, nowUtc) : null;
    }

    // The outcome is decided before the attempt's services are disposed, so a service that throws while disposing is
    // logged and the outcome stands: an escaping throw would leave the entry Finalizing, never written or released.
    private async Task DisposeAttemptScopeAsync(AsyncServiceScope attemptScope, long jobId)
    {
        try
        {
            await attemptScope.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WorkerRuntime: disposing the attempt's services threw for job {JobId}; its outcome stands.", jobId);
        }
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
