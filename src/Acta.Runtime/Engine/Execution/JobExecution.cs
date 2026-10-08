using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta.Runtime;

/// <summary>
/// The start-admit-invoke-complete lifecycle of a single attempt: the <c>start_execution</c> CAS here, then
/// <see cref="ExecutionAdmission"/>, which settles a refused attempt, <see cref="HandlerInvocation"/>, which settles
/// an admitted one, and <see cref="ExecutionCompletion"/>, which writes the <see cref="AttemptSettlement"/>. The seam
/// with <see cref="JobExecutor"/> is not "claim versus run" (the worker loop claims; both are per-attempt): the
/// executor is the frame, four disposal obligations plus the running-attempt registration that unwind whatever
/// happens here, and this is the body. "Execution" is the persisted vocabulary, frozen at 1.0; "run" is a scheduled time.
/// </summary>
internal sealed class JobExecution(
    GetJobHandler getJob,
    CancelJobHandler cancelJob,
    Execution execution,
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options,
    JobBehaviorPipeline pipeline,
    WorkerWakeupPublisher wakeupPublisher,
    ILogger log,
    JobMetrics? metrics = null
)
{
    private readonly GetJobHandler _getJob = getJob;
    private readonly Execution _execution = execution;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly ILogger _log = log;
    private readonly JobMetrics? _metrics = metrics;
    private readonly ExecutionAdmission _admission = new(log, options.Value.ConcurrencyKeyBounceDelaySeconds);
    private readonly HandlerInvocation _invocation = new(serializers, pipeline, options.Value.MaxInlinePayloadBytes, log);
    private readonly ExecutionCompletion _completion = new(execution, cancelJob, wakeupPublisher, log, metrics);

    /// <summary>
    /// Moves a claimed row to Executing and answers what the row is, not what one write said. The write is repeated
    /// like the completion, on the host token, since a failure after the claim would leave the Dispatched row under a
    /// lease the heartbeat keeps renewing. A LostClaim means only that the version moved, and the row decides what:
    /// Executing under this worker at this execution number is a start that committed and lost its answer, so the
    /// attempt proceeds; Dispatched under this worker is an operator verb that bumped the version, and the start is
    /// made again against the version the row carries now; anything else belongs to another owner, and the claim is gone.
    /// </summary>
    internal static async Task<StartExecutionAction> StartReconciledAsync(
        Execution execution,
        GetJobHandler getJob,
        ClaimedJob job,
        int workerId,
        int leaseTtlSeconds,
        ILogger log,
        JobMetrics? metrics,
        CancellationToken ct
    )
    {
        return await StartReconciledAsync(
            execution,
            getJob,
            job.JobId,
            job.ExecutionNumber,
            job.Version,
            workerId,
            leaseTtlSeconds,
            log,
            metrics,
            ct
        );
    }

    /// <summary>
    /// The reconciliation by identity alone, for a claim this worker holds without a
    /// <see cref="ClaimedJob"/> to show for it: a claim whose answer was lost after the store committed it.
    /// </summary>
    internal static async Task<StartExecutionAction> StartReconciledAsync(
        Execution execution,
        GetJobHandler getJob,
        long jobId,
        int executionNumber,
        int claimedVersion,
        int workerId,
        int leaseTtlSeconds,
        ILogger log,
        JobMetrics? metrics,
        CancellationToken ct
    )
    {
        var version = claimedVersion;
        var expiredRefusals = 0;
        while (true)
        {
            var (start, _) = await CompletionWrite.RetryAsync(
                token => execution.StartExecutionAsync(jobId, workerId, executionNumber, version, leaseTtlSeconds, token),
                log,
                jobId,
                ct,
                metrics
            );
            // AlreadyTerminal is read against the row too: on PostgreSQL a start that waited on a row
            // lock held by a concurrent verb classifies against its statement snapshot, which still
            // shows the claim's version, and answers AlreadyTerminal for a row that is Dispatched under
            // this worker at the version the verb committed.
            if (start is not (StartExecutionAction.LostClaim or StartExecutionAction.LeaseExpired or StartExecutionAction.AlreadyTerminal))
            {
                return start;
            }

            var (row, _) = await CompletionWrite.RetryAsync(async token => await getJob.ReadAsync(jobId, token), log, jobId, ct, metrics);
            var lost = start == StartExecutionAction.LeaseExpired ? StartExecutionAction.LostClaim : start;
            if (row is null || row.LeasedByWorkerId != workerId || row.ExecutionNumber != executionNumber)
            {
                return lost;
            }
            switch (row.Status)
            {
                case JobStatusCode.Executing:
                    return StartExecutionAction.Started;
                // A lapsed lease moves no version and clears no owner: the row is still this worker's to
                // progress, and the heartbeat renews it from database state, so the write is paced on the
                // retry curve until it lands or recovery takes the row and the next read says so.
                case JobStatusCode.Dispatched when start == StartExecutionAction.LeaseExpired:
                    version = row.Version;
                    await Task.Delay(CompletionWrite.Delay(++expiredRefusals), ct);
                    break;
                // A version the write was just refused at cannot be retried: the row and the refusal
                // disagree, which is a store that answers inconsistently, and looping on it would never end.
                case JobStatusCode.Dispatched when row.Version != version:
                    version = row.Version;
                    break;
                default:
                    return lost;
            }
        }
    }

    public async Task<RunOnceStatus> RunAsync(
        IServiceProvider attemptServices,
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        int workerId,
        RecurringFirePlan? fire,
        bool alreadyStarted,
        CancellationToken ct
    )
    {
        if (!alreadyStarted && !await StartAsync(job, workerId, ct))
        {
            return RunOnceStatus.NothingClaimed;
        }

        AttemptSettlement? settlement;
        try
        {
            // A fire that only ends timed pauses with nothing due takes no slot, no turn, and no handler call;
            // it settles as a re-arm that applies the advances (ScheduleWalker.PlanFire).
            settlement = fire is { NothingDue: true }
                ? new AttemptSettlement(CompletionKind.Rescheduled)
                {
                    ReasonMessage = "A timed schedule pause ended with no occurrence due; the handler did not run.",
                }
                : await _admission.AdmitAsync(descriptor, job, jobContext, ct)
                    ?? await _invocation.InvokeAsync(attemptServices, descriptor, job, jobContext, ct);
        }
        finally
        {
            // Mutual exclusion covers the handler, not the completion write: the slot admission took goes back on
            // every exit, a refusal, the handler's end, or worker shutdown. Release is best-effort, non-throwing, and
            // a no-op when no slot was taken; a failed release self-heals via the lock's TTL.
            await jobContext.ReleaseConcurrencySlotAsync(CancellationToken.None);
        }

        return settlement is null
            ? RunOnceStatus.NothingClaimed
            : await _completion.CompleteAsync(descriptor, job, jobContext, workerId, fire, settlement, ct);
    }

    private async Task<bool> StartAsync(ClaimedJob job, int workerId, CancellationToken ct)
    {
        var start = await StartReconciledAsync(_execution, _getJob, job, workerId, _leaseTtlSeconds, _log, _metrics, ct);
        if (start == StartExecutionAction.Started)
        {
            return true;
        }

        // The claim was lost before execution began: reclaimed by recovery, reassigned, or moved
        // out of Dispatched by an operator control verb between claim and start. The CAS guard
        // (incl. the claim-time version) means we mutated nothing, and the reconciliation above
        // has read that the row is no longer this worker's; clean skip, let the row be re-claimed
        // next tick.
        _log.LogInformation(
            "WorkerRuntime: lost claim on job {JobId} ({Detail}) before start: ({Outcome}); skipping.",
            job.JobId,
            $"execution number {job.ExecutionNumber}",
            start.ToString()
        );
        return false;
    }
}
