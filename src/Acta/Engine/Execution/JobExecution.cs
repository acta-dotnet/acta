using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// The start-admit-invoke-complete lifecycle of a single attempt: the <c>start_execution</c> CAS here, then
/// <see cref="ExecutionAdmission"/>, which settles a refused attempt, <see cref="HandlerInvocation"/>, which settles
/// an admitted one, and <see cref="ExecutionCompletion"/>, which writes the <see cref="AttemptSettlement"/>. The seam
/// with <see cref="JobExecutor"/> is not "claim versus run" (the worker loop claims; both are per-attempt): the
/// executor is the frame, four disposal obligations plus the in-flight entry's removal that unwind whatever
/// happens here, and this is the body. "Execution" is the persisted vocabulary, frozen at 1.0; "run" is a scheduled time.
/// </summary>
internal sealed class JobExecution(
    CancelJobHandler cancelJob,
    Execution execution,
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options,
    JobBehaviorPipeline pipeline,
    WorkerWakeupPublisher wakeupPublisher,
    ILogger log,
    ActaMetrics? metrics = null
)
{
    private readonly Execution _execution = execution;
    private readonly int _leaseTtlSeconds = options.Value.LeaseTtlSeconds;
    private readonly ILogger _log = log;
    private readonly ActaMetrics? _metrics = metrics;
    private readonly ExecutionAdmission _admission = new(log, options.Value.ConcurrencyKeyBounceDelaySeconds);
    private readonly HandlerInvocation _invocation = new(serializers, pipeline, options.Value.MaxInlinePayloadBytes, log);
    private readonly ExecutionCompletion _completion = new(execution, cancelJob, wakeupPublisher, log, metrics);

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

    /// <summary>
    /// Moves the claimed row to Executing. The write is repeated like the completion, on the host token, since a start
    /// abandoned after the claim leaves the row to wait out its lease; a start that committed and lost its answer is
    /// answered Started again.
    /// </summary>
    private async Task<bool> StartAsync(ClaimedJob job, int workerId, CancellationToken ct)
    {
        var (start, _) = await CompletionWrite.RetryAsync(
            token => _execution.StartExecutionAsync(job.JobId, workerId, job.ExecutionNumber, _leaseTtlSeconds, token),
            _log,
            job.JobId,
            ct,
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
}
