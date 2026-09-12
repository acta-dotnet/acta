using System.Diagnostics;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Jobs;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Services.Locks;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Executes one already-claimed job: resolves the descriptor, opens the per-attempt DI scope, plans
/// a recurring slot fire, builds the <see cref="RuntimeJobContext"/> and publishes it on the scope,
/// then hands the start-invoke-complete lifecycle to <see cref="JobExecution"/> (which takes the
/// exclusive-key lock after the start CAS and bounces a loser back to Ready). Claiming jobs from the
/// DB and dispatching them to executors is the worker loop's job.
/// <para>A claim this deployment carries no handler for is returned to Ready instead of executed;
/// see <see cref="ExecuteClaimedJobAsync"/>.</para>
/// </summary>
internal sealed class JobExecutor(
    ILockStore lockStore,
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
    private readonly ILockStore _lockStore = lockStore;
    private readonly IActaClock _clock = clock;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly IServiceProvider _rootServices = rootServices;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerContext _context = context;
    private readonly JobExecution _jobExecution = jobExecution;
    private readonly Acta.Runtime.Modules.Execution.IExecutionStore _execution =
        rootServices.GetRequiredService<Acta.Runtime.Modules.Execution.IExecutionStore>();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly JobMetrics? _metrics = metrics;

    // Re-arm delay for a claim this deployment cannot run, in whole seconds (the store verb's unit).
    // SafetyPollInterval is the honest unit for it: the option is documented as the bound on how long
    // a Ready row waits to be found by a process this one shares no wakeup transport with, and a
    // process that carries the missing handler is exactly who has to pick this job up. Validation
    // keeps it at or above one second, so the delay never rounds to zero.
    private readonly int _unsupportedClaimDelaySeconds = (int)Math.Ceiling(options.Value.SafetyPollInterval.TotalSeconds);

    /// <summary>
    /// Claim and run exactly one Ready job: descriptor dispatch and the start/execute/complete
    /// lifecycle (including the exclusive-key lock). The deterministic single-shot primitive: the
    /// production loop drives it from N executor loops; tests drive it directly.
    /// </summary>
    public Task<RunOnceOutcome> RunOnceAsync(string namespaceName, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, explicitJobId: null, ct);

    /// <summary>
    /// Claim and run a specific Ready job by id (via the claim's <c>ExplicitJobIds</c> path),
    /// for callers that already know which job to run. Still a single tick: the claim uses READPAST, so
    /// a transiently-locked row yields <see cref="RunOnceOutcome.NothingClaimed"/>, and the caller decides
    /// whether to tick again.
    /// </summary>
    public Task<RunOnceOutcome> RunOnceAsync(string namespaceName, long jobId, CancellationToken ct) =>
        RunOnceCoreAsync(namespaceName, jobId, ct);

    private async Task<RunOnceOutcome> RunOnceCoreAsync(string namespaceName, long? explicitJobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        var (namespaceId, workerId) = _context.ResolveWorker(namespaceName);

        var claim = explicitJobId is { } id
            ? await _execution.ClaimOneAsync(new ClaimRequest(namespaceId, workerId, MaxBatch: 1), _leaseTtlSeconds, id, ct)
            : await _execution.ClaimOneAsync(new ClaimRequest(namespaceId, workerId, MaxBatch: 1), _leaseTtlSeconds, null, ct);
        if (claim.Jobs.Count == 0)
        {
            _metrics?.RecordClaim(namespaceName, "nothing-claimed");
            return RunOnceOutcome.NothingClaimed;
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
    public async Task<RunOnceOutcome> ExecuteClaimedJobAsync(
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
            return await ReleaseUnsupportedClaimAsync(job, namespaceName, workerId, alreadyStarted, ct);
        }

        // One scope per attempt carrying the job identity. Opened on the runtime logger, which shares
        // the factory's external scope provider, so the handler's own ILogger<T> lines inherit these
        // fields too. Covers the lock, the attempt, and the finally.
        using var logScope = _log.BeginScope(
            JobLogScope.For(job.JobId, descriptor.JobName, namespaceName, job.ExecutionNumber, workerId, job.CorrelationKey)
        );

        // Link a per-attempt cancellation source off the worker token AND a dedicated timeout source; the
        // heartbeat cancels it when an external cancel or lease steal drops this job from the worker's
        // lease set, and the timeout source cancels it when the per-attempt wall-clock cap elapses. The
        // separate timeout source lets the execution tell a timeout from an external cancel. Registering the
        // attempt also lets the heartbeat extend every lock it holds.
        var timeoutCts = new CancellationTokenSource();
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var attempt = new RunningAttempt(attemptCts, timeoutCts: timeoutCts)
        {
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

        try
        {
            await using var attemptScope = _rootServices.CreateAsyncScope();
            var attemptServices = attemptScope.ServiceProvider;

            // Recurring slot fire: read live schedules once, plan the due set + cursor advances at a
            // single captured nowUtc. The due names are visible to the handler; the advances + slot
            // MIN apply at completion. Non-slot jobs keep the unchanged one-shot path.
            var isRecurring = _context.RecurringSlotJobIds.Contains(job.JobId);
            RecurringFireOutcome? fireOutcome = null;
            if (isRecurring)
            {
                var nowUtc = await _clock.GetUtcNowAsync(ct);
                var live = await _rootServices.GetRequiredService<IScheduleStore>().GetLiveSchedulesAsync(job.JobId, ct);
                fireOutcome = ScheduleWalker.PlanFire(live, nowUtc);
            }

            var backoff = Backoff.Parse(descriptor.Backoff ?? JobDefinitionRegistration.DefaultBackoffExpression);
            var stepRetryDefaults = new StepRetryDefaults(
                descriptor.MaxAttempts,
                DurationSyntax.ToWholeSeconds(backoff.InitialDelay, nameof(backoff)),
                DurationSyntax.ToWholeSeconds(backoff.MaxDelay, nameof(backoff)),
                (decimal)backoff.Multiplier,
                (decimal)backoff.Jitter
            );

            // Resolve the external tenant key off the process-lifetime cache (one point read per
            // distinct tenant); the claim projection itself stays join-free.
            var tenantKey = job.TenantId is { } jobTenantId
                ? await _rootServices
                    .GetRequiredService<Acta.Runtime.Modules.Execution.Tenants.TenantKeyCache>()
                    .ResolveAsync(jobTenantId, ct)
                : null;

            var jobContext = new RuntimeJobContext(
                job,
                descriptor.JobName,
                namespaceName,
                namespaceId,
                _options.Value.LeaseTtlSeconds,
                _rootServices.GetRequiredService<IJobStore>(),
                _rootServices.GetRequiredService<Acta.Runtime.Modules.Execution.Signals.ISignalStore>(),
                _rootServices.GetRequiredService<IAlertSink>(),
                _rootServices.GetRequiredService<Acta.Runtime.Modules.Execution.IExecutionStore>(),
                _serializers,
                _lockStore,
                fireOutcome?.TriggeringScheduleNames ?? [],
                // A recurring slot never carries the whole-job deadline: it anchors to job creation
                // and the slot row lives forever, so the Strict paths would cancel the slot
                // terminally and the advisory surface (ctx.IsOverdue, ctx.TimeUntilDeadline) would
                // report permanently overdue to every occurrence. Worker init rejects the
                // Deadline + [JobSchedule] combination; a null here covers slots that predate the
                // rule or gained a deadline through an override, in the one place every consumer
                // reads from.
                !isRecurring && descriptor.DeadlineSeconds is { } deadlineSecs && deadlineSecs > 0
                    ? job.CreatedAtUtc.AddSeconds(deadlineSecs)
                    : (DateTime?)null,
                attemptCts.Token,
                _options.Value.MaxInlinePayloadBytes,
                attempt,
                stepRetryDefaults,
                _log,
                _metrics,
                attemptServices.GetService<IJobs>(),
                tenantKey,
                workerId,
                _rootServices.GetRequiredService<WorkerWakeupPublisher>(),
                _clock
            );

            // Publish the context on the attempt scope so DI-resolved handlers (e.g. MediatR) inject it.
            // The handler resolves from this same scope, so the scoped accessor carries the set value.
            attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;

            return await _jobExecution.RunAsync(
                attemptServices,
                descriptor,
                job,
                jobContext,
                workerId,
                isRecurring,
                fireOutcome,
                alreadyStarted,
                ct
            );
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
    /// Returns a claim this deployment has no handler for to Ready: budget-neutral, lease cleared, due
    /// again after <see cref="JobsOptions.SafetyPollInterval"/>, so a worker that carries the
    /// definition can take it. Claims are selected by namespace, so a namespace that still holds jobs
    /// for a definition dropped from the manifest - and every rolling deploy - hands some worker a job
    /// it cannot run.
    /// <para>The claim was never an attempt: nothing is registered in
    /// <c>WorkerContext.RunningAttempts</c>, no per-attempt scope is opened, and no handler is
    /// invoked. Refusing the claim by throwing instead would strand the row - the claim has already
    /// stamped the lease and the in-flight status, the worker loop swallows the exception and stays
    /// healthy, and <c>extend_worker_leases</c> renews every row this worker leases from database
    /// state alone, so the lease never lapses and <c>sys.recovery</c> never reclaims it.</para>
    /// </summary>
    private async Task<RunOnceOutcome> ReleaseUnsupportedClaimAsync(
        ClaimedJob job,
        string namespaceName,
        int workerId,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        // complete_execution's CAS matches an Executing row only, so the claim is walked through the
        // start CAS first even though nothing will run. The combined claim loop already started the
        // execution in the claim itself.
        if (!alreadyStarted)
        {
            var start = await _execution.StartExecutionAsync(job.JobId, workerId, job.ExecutionNumber, job.Version, _leaseTtlSeconds, ct);
            if (start != StartExecutionAction.Started)
            {
                // The claim was lost before the bounce: reclaimed on lease expiry, reassigned, or moved
                // out of Dispatched by a control verb. The CAS guard means nothing was mutated.
                _log.LogInformation(
                    "WorkerRuntime: lost claim on job {JobId} ({Detail}) before releasing it: ({Outcome}); skipping.",
                    job.JobId,
                    $"definition_id {job.DefinitionId}",
                    start.ToString()
                );
                return RunOnceOutcome.NothingClaimed;
            }
        }

        // Unclassified rather than a borrowed code: no catalog reason describes a worker declining a
        // claim, and the two candidates would both misreport it - JobAttemptAborted is published as a
        // mid-flight abort retried under the failure budget, JobDefinitionRetired as a catalog
        // retirement that cancels. Unclassified is the writer saying the story is in the message, and
        // it stays out of both the alertable set and the Failures audit level, which a bounce belongs
        // outside of: nothing failed.
        var request = new CompleteExecutionRequest(
            job.JobId,
            workerId,
            job.ExecutionNumber,
            ExecutionOutcome.Rescheduled,
            0,
            ReadOnlyMemory<byte>.Empty,
            JobEventReasonCode.Unclassified,
            $"No handler for definition_id={job.DefinitionId} in this deployment; returned to Ready for a worker that has one.".Truncate(
                ActaTextLimits.ReasonMessage
            ),
            DurationMs: 0
        )
        {
            RescheduleStatusCode = (byte)ExecutionStatusCode.Rescheduled,
            RescheduleDelaySeconds = _unsupportedClaimDelaySeconds,
        };

        // One warning per bounce: the ping-pong between an incapable worker and the queue is bounded
        // by the delay but otherwise invisible, and this is the only place the pair (namespace,
        // definition id) is known. The job name is not - resolving it is what the missing descriptor
        // would have done - so the ref is what an operator takes to `jobs explain`.
        _log.LogWarning(
            "WorkerRuntime: ({Namespace}) job {JobId} ({Detail}) claimed with no handler in this deployment; returned to Ready in {DurationMs}ms.",
            namespaceName,
            job.JobId,
            $"ref {job.JobRef}, definition_id {job.DefinitionId}",
            _unsupportedClaimDelaySeconds * 1000
        );

        // No wakeup publish, deliberately: every claim loop re-polls within SafetyPollInterval, which
        // is the delay itself, and waking this namespace would wake this worker's own loops first and
        // tighten the bounce into a spin.
        var complete = await _execution.CompleteExecutionAsync(request, ct);
        return complete.Action == CompleteExecutionAction.Completed ? RunOnceOutcome.Rearmed : RunOnceOutcome.NothingClaimed;
    }
}
