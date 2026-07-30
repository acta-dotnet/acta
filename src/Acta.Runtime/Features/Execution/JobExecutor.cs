using System.Diagnostics;
using Acta.Configuration;
using Acta.Features.Definitions;
using Acta.Features.Execution;
using Acta.Features.Jobs;
using Acta.Features.Schedules;
using Acta.Features.Workers;
using Acta.Payloads;
using Acta.Services.Locks;
using Acta.Services.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Features.Execution;

/// <summary>
/// Executes one already-claimed job: resolves the descriptor, opens the per-attempt DI scope, plans
/// a recurring slot fire, builds the <see cref="RuntimeJobContext"/> and publishes it on the scope,
/// then hands the start-invoke-complete lifecycle to <see cref="JobRunner"/> (which takes the
/// exclusive-key lock after the start CAS and bounces a loser back to Ready). Claiming jobs from the
/// DB and dispatching them to executors is the worker loop's job.
/// </summary>
internal sealed class JobExecutor
{
    private readonly int _leaseTtlSeconds;
    private readonly ILockStore _lockStore;
    private readonly IActaClock _clock;
    private readonly IJobPayloadSerializerRegistry _serializers;
    private readonly IServiceProvider _rootServices;
    private readonly IOptions<JobsOptions> _options;
    private readonly WorkerContext _context;
    private readonly JobRunner _runner;
    private readonly Acta.Features.Execution.IExecutionStore _execution;
    private readonly ILogger _log;
    private readonly JobMetrics? _metrics;

    public JobExecutor(
        ILockStore lockStore,
        IActaClock clock,
        IJobPayloadSerializerRegistry serializers,
        IServiceProvider rootServices,
        IOptions<JobsOptions> options,
        WorkerContext context,
        JobRunner runner,
        ILogger? log = null,
        JobMetrics? metrics = null
    )
    {
        _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
        _lockStore = lockStore;
        _clock = clock;
        _serializers = serializers;
        _rootServices = rootServices;
        _options = options;
        _context = context;
        _runner = runner;
        _execution = rootServices.GetRequiredService<Acta.Features.Execution.IExecutionStore>();
        _log = log ?? NullLogger.Instance;
        _metrics = metrics;
    }

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

    public async Task<RunOnceOutcome> ExecuteClaimedJobAsync(
        ClaimedJob job,
        string namespaceName,
        short namespaceId,
        int workerId,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        if (!_context.DescriptorByDefinitionId.TryGetValue(job.DefinitionId, out var descriptor))
        {
            throw new InvalidOperationException(
                $"Claimed job with definition_id={job.DefinitionId} (job {job.JobId}) "
                    + "has no descriptor binding. Was AddManifest called for the right manifest before InitializeAsync?"
            );
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
        // separate timeout source lets the runner tell a timeout from an external cancel. Registering the
        // attempt also lets the heartbeat extend every lock it holds.
        var timeoutCts = new CancellationTokenSource();
        var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var attempt = new RunningAttempt(attemptCts, timeoutCts: timeoutCts);
        // Seed the monotonic job-lease deadline. The claim stamped the DB lease at most `now` (dispatch
        // runs after the claim), so now + LeaseTtl is a slight over-estimate of it; the first worker
        // heartbeat re-seeds it conservatively from that renewal's request-start, and the watchdog's
        // unwind margin absorbs the seed's slack in the meantime.
        attempt.JobLeaseGoodUntil = Stopwatch.GetTimestamp() + (long)(_leaseTtlSeconds * (double)Stopwatch.Frequency);
        var timeoutSeconds = descriptor.ExecutionTimeoutSeconds ?? JobDefinitionRegistration.DefaultExecutionTimeoutSeconds;
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
                ? await _rootServices.GetRequiredService<Acta.Features.Tenants.TenantKeyCache>().ResolveAsync(jobTenantId, ct)
                : null;

            var jobContext = new RuntimeJobContext(
                job,
                descriptor.JobName,
                namespaceName,
                namespaceId,
                _options.Value.LeaseTtlSeconds,
                _rootServices.GetRequiredService<IJobStore>(),
                _rootServices.GetRequiredService<Acta.Features.Signals.ISignalStore>(),
                _rootServices.GetRequiredService<Acta.Features.Alerts.IAlertStore>(),
                _rootServices.GetRequiredService<Acta.Features.Execution.IExecutionStore>(),
                _serializers,
                _lockStore,
                _clock,
                _options.Value.AlertDedupeWindow,
                attemptCts.Token,
                fireOutcome?.TriggeringScheduleNames ?? [],
                descriptor.DeadlineSeconds is { } deadlineSecs && deadlineSecs > 0
                    ? job.CreatedAtUtc.AddSeconds(deadlineSecs)
                    : (DateTime?)null,
                _options.Value.MaxInlinePayloadBytes,
                attempt,
                stepRetryDefaults,
                _log,
                _metrics,
                attemptServices.GetService<IJobs>(),
                tenantKey
            );

            // Publish the context on the attempt scope so DI-resolved handlers (e.g. MediatR) inject it.
            // The handler resolves from this same scope, so the scoped accessor carries the set value.
            attemptServices.GetRequiredService<IJobContextAccessor>().JobContext = jobContext;

            return await _runner.RunAsync(
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
}
