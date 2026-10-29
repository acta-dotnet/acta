using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// Every log line Acta writes, as source-generated <see cref="LoggerMessageAttribute"/> methods, so a disabled level costs
/// one check and no formatting. Each parameter is a field in an operator's index, named and typed from the vocabulary
/// <c>LogVocabularyTests</c> holds. The success path logs nothing per job; failures, misconfigurations and rare events do.
/// </summary>
internal static partial class ActaLog
{
    /// <summary>
    /// Opens the per-attempt scope that stamps the job's identity on every line written while it runs. Loggers from one
    /// factory share an external scope provider, so the handler's own <c>ILogger&lt;T&gt;</c> lines carry it too.
    /// </summary>
    public static IDisposable? BeginJobScope(
        ILogger log,
        long actaJobId,
        string actaJobName,
        string actaNamespace,
        long actaExecutionNumber,
        long actaWorkerId,
        string? actaCorrelationKey
    ) => log.BeginScope(JobScopeState(actaJobId, actaJobName, actaNamespace, actaExecutionNumber, actaWorkerId, actaCorrelationKey));

    internal static IReadOnlyList<KeyValuePair<string, object>> JobScopeState(
        long actaJobId,
        string actaJobName,
        string actaNamespace,
        long actaExecutionNumber,
        long actaWorkerId,
        string? actaCorrelationKey
    )
    {
        var state = new List<KeyValuePair<string, object>>(6)
        {
            new("ActaJobId", actaJobId),
            new("ActaJobName", actaJobName),
            new("ActaNamespace", actaNamespace),
            new("ActaExecutionNumber", actaExecutionNumber),
            new("ActaWorkerId", actaWorkerId),
        };
        if (actaCorrelationKey is not null)
        {
            state.Add(new("ActaCorrelationKey", actaCorrelationKey));
        }

        return state;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Acta worker started in namespace ({ActaNamespace}) ({ActaDetail}).")]
    public static partial void WorkerStarted(ILogger log, string actaNamespace, string actaDetail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Acta: enqueue-only process; no workers registered.")]
    public static partial void EnqueueOnlyProcess(ILogger log);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: claim iteration failed; backing off {ActaDurationMs}ms before retry.")]
    public static partial void ClaimIterationFailed(ILogger log, Exception exception, long actaDurationMs);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WorkerRuntime: admission pass failed for namespace ({ActaNamespace}); retrying after the safety poll."
    )]
    public static partial void AdmissionPassFailed(ILogger log, Exception exception, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: a schedule of job {ActaJobName} cannot be evaluated and is retried after a minute ({ActaDetail})."
    )]
    public static partial void ScheduleEvaluationFailed(ILogger log, string actaJobName, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta worker clock skew {ActaDurationMs}ms exceeds the warn threshold for namespace ({ActaNamespace}) ({ActaDetail}); lease and schedule timing may drift."
    )]
    public static partial void ClockSkewOverWarnThreshold(ILogger log, long actaDurationMs, string actaNamespace, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta worker clock skew {ActaDurationMs}ms exceeds the fail threshold for namespace ({ActaNamespace}) ({ActaDetail}), but AllowClockSkew is set; proceeding."
    )]
    public static partial void ClockSkewOverFailThresholdAllowed(ILogger log, long actaDurationMs, string actaNamespace, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to release the ({ActaDetail}) held for job {ActaJobId}; continuing because the lock TTL will clean it up."
    )]
    public static partial void LockReleaseFailed(ILogger log, Exception exception, string actaDetail, long actaJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Step on job {ActaJobId} exhausted its attempts ({ActaDetail}).")]
    public static partial void StepExhausted(ILogger log, long actaJobId, string actaDetail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step on job {ActaJobId} failed and retries ({ActaDetail}).")]
    public static partial void StepRetrying(ILogger log, long actaJobId, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Child wait timeout cancel skipped: the awaited job ({ActaDetail}) is not a child of this one, so its wait could only ever expire."
    )]
    public static partial void ChildWaitCancelSkipped(ILogger log, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WorkerRuntime: job {ActaJobId} overdue at admission; cancelling without running the handler."
    )]
    public static partial void JobOverdueAtAdmission(ILogger log, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: concurrency admission bounced job {ActaJobId}; the slot acquire for concurrency key ({ActaDetail}) failed, so the job re-armed Ready in {ActaDurationMs}ms."
    )]
    public static partial void ConcurrencyAdmissionFailed(
        ILogger log,
        Exception exception,
        long actaJobId,
        string actaDetail,
        long actaDurationMs
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: rate admission bounced job {ActaJobId}; the reservation for rate name ({ActaDetail}) failed, so the job re-armed Ready in {ActaDurationMs}ms."
    )]
    public static partial void RateAdmissionFailed(
        ILogger log,
        Exception exception,
        long actaJobId,
        string actaDetail,
        long actaDurationMs
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WorkerRuntime: job {ActaJobId} interrupted by worker shutdown; left Executing for recovery."
    )]
    public static partial void JobInterruptedByShutdown(ILogger log, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WorkerRuntime: job {ActaJobId} lost ownership of step ({ActaDetail}) mid-flight; aborting the attempt cooperatively."
    )]
    public static partial void StepOwnershipLost(ILogger log, long actaJobId, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler for job ({ActaJobName}) id {ActaJobId} did not handle StepInterruptedException for step ({ActaDetail}); failing terminally."
    )]
    public static partial void StepInterruptUnhandled(ILogger log, string actaJobName, long actaJobId, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WorkerRuntime: job {ActaJobId} attempt {ActaExecutionNumber} lost the job to another execution; a write was fenced out and the attempt stopped without a completion."
    )]
    public static partial void AttemptFenced(ILogger log, long actaJobId, long actaExecutionNumber);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler result for job ({ActaJobName}) id {ActaJobId} exceeds the inline cap ({ActaDetail}); the result body was dropped."
    )]
    public static partial void ResultDroppedOverCap(ILogger log, string actaJobName, long actaJobId, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Handler for job ({ActaJobName}) id {ActaJobId} threw a control signal the runtime does not recognize; transitioning to Failed."
    )]
    public static partial void UnrecognizedControlSignal(ILogger log, Exception exception, string actaJobName, long actaJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handler for job ({ActaJobName}) id {ActaJobId} exceeded its execution timeout.")]
    public static partial void ExecutionTimedOut(ILogger log, string actaJobName, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: job {ActaJobId} attempt aborted mid-flight; handler stopped cooperatively."
    )]
    public static partial void AttemptAborted(ILogger log, long actaJobId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handler for job ({ActaJobName}) id {ActaJobId} threw; transitioning to Failed.")]
    public static partial void HandlerThrew(ILogger log, Exception exception, string actaJobName, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Input deserialization for job ({ActaJobName}) id {ActaJobId} failed; transitioning the attempt to Failed."
    )]
    public static partial void InputDeserializationFailed(ILogger log, Exception exception, string actaJobName, long actaJobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: executor faulted on job {ActaJobId}.")]
    public static partial void ExecutorFaulted(ILogger log, Exception exception, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: ({ActaNamespace}) job {ActaJobId} ({ActaDetail}) claimed with no handler in this deployment; returned to Ready in {ActaDurationMs}ms; excluded from this worker's claims until restart."
    )]
    public static partial void NoHandlerDeployed(ILogger log, string actaNamespace, long actaJobId, string actaDetail, long actaDurationMs);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WorkerRuntime: attempt setup failed before the handler ran for job {ActaJobId}; returning it to Ready."
    )]
    public static partial void AttemptSetupFailed(ILogger log, Exception exception, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WorkerRuntime: disposing the attempt's services threw for job {ActaJobId}; its outcome stands."
    )]
    public static partial void AttemptServicesDisposeFailed(ILogger log, Exception exception, long actaJobId);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: finalizer faulted on ({ActaDetail}).")]
    public static partial void FinalizerFaulted(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: a batch of ({ActaDetail}) kept failing; writing each alone.")]
    public static partial void CompletionBatchFailing(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "WorkerRuntime: job {ActaJobId}'s completion keeps failing alone; it repeats apart from the others."
    )]
    public static partial void CompletionFailingAlone(ILogger log, Exception exception, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "WorkerRuntime: ({ActaNamespace}) job {ActaJobId} completion skipped ({ActaDetail})."
    )]
    public static partial void CompletionSkipped(ILogger log, string actaNamespace, long actaJobId, string actaDetail);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: follow-ups for job {ActaJobId} failed after its completion landed.")]
    public static partial void FollowUpsFailed(ILogger log, Exception exception, long actaJobId);

    [LoggerMessage(
        Message = "WorkerRuntime: a database call for job {ActaJobId} failed ({ActaDetail}); retrying in {ActaDurationMs}ms until it lands or this worker stops."
    )]
    public static partial void DatabaseCallRetrying(
        ILogger log,
        LogLevel level,
        Exception exception,
        long actaJobId,
        string actaDetail,
        long actaDurationMs
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: initial heartbeat tick failed; retrying on the interval.")]
    public static partial void HeartbeatInitialTickFailed(ILogger log, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "WorkerRuntime: heartbeat tick failed; retrying next tick.")]
    public static partial void HeartbeatTickFailed(ILogger log, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "WorkerRuntime: could not extend worker leases this tick; the lease deadlines stand."
    )]
    public static partial void LeaseExtensionFailed(ILogger log, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Namespace ({ActaNamespace}): the recovery pass failed; the next one runs when it comes due."
    )]
    public static partial void RecoveryPassFailed(ILogger log, Exception exception, string actaNamespace);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WorkerRuntime: cancelling job {ActaJobId}; a held lock was lost.")]
    public static partial void HeldLockLost(ILogger log, long actaJobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta: the ({ActaDetail}) stamp failed for worker namespace ({ActaNamespace}); lease recovery will reconcile it."
    )]
    public static partial void WorkerStampFailed(ILogger log, Exception exception, string actaDetail, string? actaNamespace);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acta: ({ActaDetail}) stamps exceeded their shutdown budget; continuing shutdown.")]
    public static partial void WorkerStampsOverBudget(ILogger log, string actaDetail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acta: a worker loop faulted during drain; continuing shutdown.")]
    public static partial void WorkerLoopFaultedDuringDrain(ILogger log, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Registering the catalog failed ({ActaDetail}); another replica may be registering it. Retrying."
    )]
    public static partial void CatalogRegistrationRetrying(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.alerts: transport kind ({ActaDetail}) threw delivering alert ({ActaAlertRef}); will retry."
    )]
    public static partial void AlertTransportThrew(ILogger log, Exception exception, string actaDetail, string actaAlertRef);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.alerts: channel ({ActaDetail}) is not configured for namespace ({ActaNamespace}); marking alert ({ActaAlertRef}) failed."
    )]
    public static partial void AlertChannelNotConfigured(ILogger log, string actaDetail, string actaNamespace, string actaAlertRef);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.alerts: no transport is registered for ({ActaDetail}); marking alert ({ActaAlertRef}) delivery failed."
    )]
    public static partial void AlertTransportMissing(ILogger log, string actaDetail, string actaAlertRef);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ACTA sys.alerts: alert ({ActaAlertRef}) in namespace ({ActaNamespace}) is suppressed: channel ({ActaDetail})."
    )]
    public static partial void AlertSuppressedByChannel(ILogger log, string actaAlertRef, string actaNamespace, string actaDetail);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACTA sys.alerts: alert ({ActaAlertRef}) is suppressed: ({ActaDetail}).")]
    public static partial void AlertSuppressedBySeverity(ILogger log, string actaAlertRef, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta alerting: job ({ActaJobName}) routes to unconfigured channel ({ActaDetail}) in namespace ({ActaNamespace}); add it to worker startup configuration with w.AddAlertChannel(...)."
    )]
    public static partial void AlertRouteToUnconfiguredChannel(ILogger log, string actaJobName, string actaDetail, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ACTA sys.alerts: the generate drain stopped at its bound in namespace ({ActaNamespace}) after {ActaDurationMs} ms ({ActaDetail}); the next pass resumes from the cursor."
    )]
    public static partial void AlertDrainStoppedAtBound(ILogger log, string actaNamespace, long actaDurationMs, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ACTA sys.alerts: replayed skipped alert event ({ActaDetail}) in namespace ({ActaNamespace})."
    )]
    public static partial void AlertEventReplayed(ILogger log, string actaDetail, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.alerts: skipped poison alert event ({ActaDetail}) in namespace ({ActaNamespace})."
    )]
    public static partial void PoisonAlertEventSkipped(ILogger log, Exception exception, string actaDetail, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Slack channel ({ActaDetail}) has no endpoint; cannot deliver alert ({ActaAlertRef})."
    )]
    public static partial void SlackChannelHasNoEndpoint(ILogger log, string actaDetail, string actaAlertRef);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Slack delivery to channel ({ActaDetail}) failed transiently for alert ({ActaAlertRef})."
    )]
    public static partial void SlackDeliveryFailed(ILogger log, Exception exception, string actaDetail, string actaAlertRef);

    [LoggerMessage(Message = "ACTA ALERT ns={ActaNamespace} alert={ActaAlertRef} job={ActaJobRef} kind={ActaAlertKind}: ({ActaDetail})")]
    public static partial void AlertNotified(
        ILogger log,
        LogLevel level,
        string actaNamespace,
        string actaAlertRef,
        string? actaJobRef,
        string actaAlertKind,
        string actaDetail
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis wake publish failed for channel ({ActaDetail}); remote waiters fall back to their poll floors."
    )]
    public static partial void RedisPublishFailed(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis wake subscription failed; this process wakes on its poll floors until a wait retries the subscription."
    )]
    public static partial void RedisSubscribeFailed(ILogger log, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis wake relay failed for channel ({ActaDetail}).")]
    public static partial void RedisRelayFailed(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Wake publish failed for channel ({ActaDetail}); relying on the waiter's poll floor."
    )]
    public static partial void WakePublishFailed(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Namespace ({ActaNamespace}): crash recovery is off, so crashed workers are never marked dead and their in-flight jobs are never reclaimed. Those jobs stay Executing behind a lapsed lease permanently. Set JobsOptions.RegisterSystemJobs = true, or run an equivalent reclaim sweep yourself."
    )]
    public static partial void CrashRecoveryOff(ILogger log, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Namespace ({ActaNamespace}): recurring job ({ActaJobName}) is paused by an operator and stays paused; it runs again only when an operator resumes it."
    )]
    public static partial void RecurringJobPaused(ILogger log, string actaNamespace, string actaJobName);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta manifest generation ({ActaDetail}) was derived from the deployed file's last-write time; container image copies and artifact restores can skew it and misorder definition promotion. Set JobsOptions.ManifestGenerationUtc to a CI build stamp for deterministic ordering."
    )]
    public static partial void ManifestGenerationFromFileTime(ILogger log, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Payload contract drift for job ({ActaJobName}) in namespace ({ActaNamespace}): ({ActaDetail}). Enqueues made from now on use the new contract."
    )]
    public static partial void PayloadContractDrift(ILogger log, string actaJobName, string actaNamespace, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta rate limiting: meter ({ActaDetail}) has participants at different effective rates in namespace ({ActaNamespace}); a meter whose participants disagree is retuned by overriding any participant."
    )]
    public static partial void MeterRatesDisagree(ILogger log, string actaDetail, string actaNamespace);

    [LoggerMessage(Level = LogLevel.Error, Message = "ACTA sys.outbox: the relay quarantined rows this tick ({ActaDetail}).")]
    public static partial void OutboxRowsQuarantined(ILogger log, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.outbox: target enqueue failed for source ({ActaDetail}); releasing claim and failing tick."
    )]
    public static partial void OutboxTargetEnqueueFailed(ILogger log, Exception exception, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ACTA sys.outbox: rejected a row group in namespace ({ActaNamespace}) ({ActaDetail})."
    )]
    public static partial void OutboxRowGroupRejected(ILogger log, string actaNamespace, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ACTA sys.retention: purged ({ActaDetail}) in namespace ({ActaNamespace}) that aged out before delivery settled (alert-retention-cap)."
    )]
    public static partial void UndeliveredAlertsPurged(ILogger log, string actaDetail, string actaNamespace);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta: driver-version-preflight found an uncertified database driver ({ActaDetail}) (driver-major-mismatch)."
    )]
    public static partial void UncertifiedDriver(ILogger log, string actaDetail);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta: rolling back a failed database attempt threw while disposing its transaction; teardown continues and the attempt's own failure is what propagates to the caller."
    )]
    public static partial void TransactionRollbackFailed(ILogger log, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Acta: disposing the connection of a failed database attempt threw; that connection may never return to the pool, and the attempt's own failure is what propagates to the caller."
    )]
    public static partial void ConnectionDisposeFailed(ILogger log, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acta: disposing an unbound command failed.")]
    public static partial void UnboundCommandDisposeFailed(ILogger log, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Acta: disposing a failed command or reader failed.")]
    public static partial void CommandDisposeFailed(ILogger log, Exception exception);
}
