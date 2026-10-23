using Microsoft.Extensions.Logging;

namespace Acta;

/// <summary>
/// The gates an attempt passes after the start CAS and before its handler: a Strict deadline already past, the
/// concurrency slot, the rate turn, and the deadline again after a rate wait. An attempt the gates turn away completes
/// without its handler.
/// </summary>
internal sealed class ExecutionAdmission(ILogger log, int concurrencyKeyBounceDelaySeconds)
{
    /// <summary>
    /// The longest rate turn an executor sleeps out in process rather than re-arming for: a quarter of
    /// a second, about the claim-path pickup latency, so a nearer turn is taken to the millisecond at
    /// no more executor time than a re-arm would cost, while a farther one re-arms and frees the
    /// executor for other work; a fleet of executors each holding a metered job for a whole second
    /// starved unmetered jobs enqueued beside the backlog. Well inside the second a booked turn stays
    /// valid past its instant, so a re-armed turn is still honoured when the worker returns for it.
    /// </summary>
    internal const int RateTurnWaitMilliseconds = 250;

    private static bool IsNearTurn(ReserveRateResponse reservation) =>
        !reservation.Admitted && reservation.WaitMilliseconds > 0 && reservation.WaitMilliseconds <= RateTurnWaitMilliseconds;

    /// <summary>The completion of an attempt the gates turn away, or null when it is admitted and its handler runs.</summary>
    public async Task<ExecutionCompletion?> AdmitAsync(
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        CancellationToken ct
    )
    {
        // Admission deadline check: a Strict job that is already past its deadline at the moment
        // execution would start is terminated Cancelled without invoking the handler. Advisory is a
        // no-op here; the handler runs regardless and the caller decides what to do.
        // The deadline anchor is DB-stamped (job.created_at_utc); comparing it against the worker
        // clock here is deliberate and bounded by the worker-init clock-skew guard, trading a small
        // skew sensitivity for not paying a DB round-trip on every admission.
        // Recurring slots never reach this branch: WorkerExecutor builds their context with a null
        // DeadlineAtUtc, so the whole-job deadline cannot cancel a slot here or on the retry guard.
        if (PastStrictDeadline(descriptor, jobContext))
        {
            return DeadlineExceeded(job);
        }

        // Concurrency admission: the slot is a lock-store row taken here, after the start CAS proved
        // ownership, and held only while the handler runs. The gate is the key - the enqueue's key
        // when the row carries one, else the definition name once the definition declares a limit -
        // and the limit is how many of its slots exist, 1 when only a key was given. A loser skips
        // the handler and completes the attempt as a budget-neutral re-arm with the fixed bounce delay
        // (the contention throttle): mutual exclusion of execution only, no per-key ordering.
        // The limit comes off the descriptor, which the policy reload keeps current, so admission
        // costs no join on the claim path.
        var concurrencyKey = job.ConcurrencyKey ?? (descriptor.ConcurrencyLimit is not null ? descriptor.JobName : null);
        if (
            concurrencyKey is not null
            && await TakeSlotAsync(job, jobContext, concurrencyKey, descriptor.ConcurrencyLimit ?? 1, ct) is { } bounced
        )
        {
            return bounced;
        }

        // Rate admission, after the concurrency slot because the rate is the scarcer gate: a job that
        // is early gives its slot straight back, so a held slot never waits out someone else's turn.
        // A denial is not a race to re-run: the meter has already booked this job's instant, so the
        // attempt completes as a budget-neutral re-arm at exactly that instant and comes back once.
        // The parsed rate rides on the descriptor, which the policy reload keeps current.
        if (descriptor.Rate is { } rate && await TakeTurnAsync(descriptor, job, jobContext, rate, ct) is { } limited)
        {
            return limited;
        }

        // A rate wait can outlast a Strict deadline that was still ahead at admission; the re-arm
        // path rechecked it on the next attempt, so the in-process path rechecks it here.
        return PastStrictDeadline(descriptor, jobContext) ? DeadlineExceeded(job) : null;
    }

    private static bool PastStrictDeadline(JobDescriptor descriptor, RuntimeJobContext jobContext) =>
        descriptor.DeadlineBehavior == DeadlineBehaviorCode.Strict && jobContext.IsOverdue;

    private ExecutionCompletion DeadlineExceeded(ClaimedJob job)
    {
        log.LogInformation("WorkerRuntime: job {JobId} overdue at admission; cancelling without running the handler.", job.JobId);
        return ExecutionCompletion.Terminal(
            CompletionKind.Cancelled,
            EventReason.DeadlineExceeded,
            "Job passed its deadline before execution started.",
            JobStatusCode.Cancelled
        );
    }

    /// <summary>
    /// Concurrency bounce: complete the attempt as a budget-neutral re-arm with the fixed delay. A provider
    /// error says nothing about the key, which a rate-only definition does not even have.
    /// </summary>
    private ExecutionCompletion Bounced(bool admissionFailed) =>
        new(CompletionKind.Rescheduled)
        {
            Reason = admissionFailed ? null : EventReason.ConcurrencyKeyHeld,
            ReasonMessage = admissionFailed
                ? "Admission failed on a provider error; the attempt re-armed instead of running."
                : "Every concurrency slot for this key is held by another execution.",
            RescheduleDelaySeconds = concurrencyKeyBounceDelaySeconds,
        };

    /// <summary>Takes one of the key's slots; the bounce completion when every slot is held or the acquire failed.</summary>
    private async Task<ExecutionCompletion?> TakeSlotAsync(
        ClaimedJob job,
        RuntimeJobContext jobContext,
        string concurrencyKey,
        int concurrencyLimit,
        CancellationToken ct
    )
    {
        try
        {
            if (await jobContext.TryAcquireConcurrencySlotAsync(concurrencyKey, concurrencyLimit, ct))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An admission error must not escape: the row is Executing under this worker, so an
            // escaped exception would leave it to lapse and recovery would charge an attempt that
            // never ran. The attempt bounces instead. A slot the acquire may have committed without
            // answering is untracked and expires with its lease TTL.
            log.LogWarning(
                ex,
                "WorkerRuntime: ({Operation}) ({Outcome}) job {JobId} ({Reason}); the slot acquire for concurrency key ({Detail}) failed, so the job re-armed Ready in {DurationMs}ms.",
                "concurrency-admission",
                "Bounced",
                job.JobId,
                "admission-error",
                concurrencyKey,
                concurrencyKeyBounceDelaySeconds * 1000
            );
            return Bounced(admissionFailed: true);
        }

        log.LogDebug(
            "WorkerRuntime: ({Operation}) ({Outcome}) job {JobId} ({Reason}); concurrency key ({Detail}) has every slot held, so the job re-armed Ready in {DurationMs}ms.",
            "concurrency-admission",
            "Bounced",
            job.JobId,
            "slots-held",
            concurrencyKey,
            concurrencyKeyBounceDelaySeconds * 1000
        );
        return Bounced(admissionFailed: false);
    }

    /// <summary>Reserves the job's turn on its rate meter; the re-arm completion when the turn lies ahead or the reservation failed.</summary>
    private async Task<ExecutionCompletion?> TakeTurnAsync(
        JobDescriptor descriptor,
        ClaimedJob job,
        RuntimeJobContext jobContext,
        RateLimitSpec rate,
        CancellationToken ct
    )
    {
        var rateName = descriptor.RateName ?? descriptor.JobName;
        ReserveRateResponse reservation;
        try
        {
            reservation = await jobContext.ReserveRateAsync(rateName, rate, ct);
            if (IsNearTurn(reservation))
            {
                reservation = await WaitForTurnAsync(job, jobContext, rateName, rate, reservation, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same rule as the slot: the error becomes a bounce with the fixed delay. A reservation the
            // meter may have booked without answering is honoured when the job returns, so nothing is
            // double-counted.
            log.LogWarning(
                ex,
                "WorkerRuntime: ({Operation}) ({Outcome}) job {JobId} ({Reason}); the reservation for rate name ({Detail}) failed, so the job re-armed Ready in {DurationMs}ms.",
                "rate-admission",
                "Bounced",
                job.JobId,
                "admission-error",
                rateName,
                concurrencyKeyBounceDelaySeconds * 1000
            );
            return Bounced(admissionFailed: true);
        }

        if (reservation.Admitted)
        {
            return null;
        }

        log.LogDebug(
            "WorkerRuntime: ({Operation}) ({Outcome}) job {JobId} ({Reason}); rate name ({Detail}) is at its limit, so the job re-armed Ready in {DurationMs}ms.",
            "rate-admission",
            "Bounced",
            job.JobId,
            "turn-reserved",
            rateName,
            (int)Math.Max(0, (reservation.ResumeAtUtc - DateTime.UtcNow).TotalMilliseconds)
        );
        // An absolute instant rather than a delay, because the turn is a place in the meter's queue, not a cooldown.
        return new ExecutionCompletion(CompletionKind.Rescheduled)
        {
            Reason = EventReason.RateLimited,
            ReasonMessage = "The rate limit for this key reserved this job its next turn.",
            RescheduleResumeAtUtc = reservation.ResumeAtUtc,
        };
    }

    /// <summary>
    /// A turn inside the next quarter second is slept out here, on the wait the meter measured on
    /// its own clock, and then collected: no re-arm, no claim round trip, and the turn is
    /// taken to the millisecond. A farther turn goes back through the claim path, where a
    /// one-second validity window covers the worker's pickup latency when it returns.
    /// </summary>
    private async Task<ReserveRateResponse> WaitForTurnAsync(
        ClaimedJob job,
        RuntimeJobContext jobContext,
        string rateName,
        RateLimitSpec rate,
        ReserveRateResponse reservation,
        CancellationToken ct
    )
    {
        log.LogDebug(
            "WorkerRuntime: ({Operation}) ({Outcome}) job {JobId} ({Reason}); rate name ({Detail}) admits it in {DurationMs}ms, waiting in process.",
            "rate-admission",
            "Waiting",
            job.JobId,
            "turn-near",
            rateName,
            reservation.WaitMilliseconds
        );
        using var turnWait = CancellationTokenSource.CreateLinkedTokenSource(ct, jobContext.CancellationToken);
        try
        {
            await Task.Delay((int)reservation.WaitMilliseconds, turnWait.Token);
            return await jobContext.ReserveRateAsync(rateName, rate, turnWait.Token);
        }
        // Worker shutdown mid-wait propagates: the row stays Executing for recovery like any attempt
        // shutdown interrupts, and the slot goes back as the attempt unwinds.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The attempt was cancelled while it waited: an operator cancel, a stolen lease, or
            // the timeout. Admitted on paper, so the handler invocation completes it the way it
            // completes any attempt cancelled before its handler ran; the booked turn stays on
            // the row for a return inside the window or for the sweep.
            return new ReserveRateResponse(true, reservation.ResumeAtUtc, 0);
        }
    }
}
