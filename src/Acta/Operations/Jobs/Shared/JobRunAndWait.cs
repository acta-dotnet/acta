using System.Diagnostics;

namespace Acta;

/// <summary>
/// <see cref="IJobs.WaitAsync"/> and <see cref="IJobs.RunAndWaitAsync{TInput}(TInput, JobExecutionOptions, CancellationToken)"/> over the public operations: a run validates
/// its wait options before anything is enqueued and enqueues, then both wait client-side for a terminal status. The
/// budget is local monotonic time, not the Acta clock: the job keeps running after the caller stops waiting.
/// </summary>
internal static class JobRunAndWait
{
    private static readonly JobExecutionOptions Defaults = new();

    public static async ValueTask<JobOutcome> RunAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
    {
        options = Validated(options);
        var enqueued = await jobs.EnqueueAsync(request(options), ct);
        var wait = await AwaitAsync(jobs, wakeup, enqueued.JobId, options.WaitTimeout, options.PollInterval, ct);
        return Outcome(wait.Snapshot ?? throw Vanished(enqueued.JobRef), wait.TimedOut);
    }

    public static async ValueTask<JobOutcome<TResult>> RunAsync<TResult>(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobRequestRouting routing,
        GetJobResultHandler getJobResult,
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
        where TResult : notnull
    {
        options = Validated(options);
        var enqueued = await jobs.EnqueueAsync(request(options), ct);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var wait = await AwaitAsync(
                jobs,
                wakeup,
                enqueued.JobId,
                Remaining(options.WaitTimeout, started),
                options.PollInterval,
                ct,
                ResultTypeCheck<TResult>(routing)
            );
            var snapshot = wait.Snapshot ?? throw Vanished(enqueued.JobRef);
            if (await OutcomeAsync<TResult>(jobs, routing, getJobResult, snapshot, wait.TimedOut, ct) is { } outcome)
            {
                return outcome;
            }
        }
    }

    public static async ValueTask<JobOutcome> WaitAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobLookup job,
        TimeSpan? timeout,
        CancellationToken ct
    )
    {
        var (snapshot, timedOut) = await AwaitExistingAsync(jobs, wakeup, job, timeout, ct);
        return Outcome(snapshot, timedOut);
    }

    public static async ValueTask<JobOutcome<TResult>> WaitAsync<TResult>(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobRequestRouting routing,
        GetJobResultHandler getJobResult,
        JobLookup job,
        TimeSpan? timeout,
        CancellationToken ct
    )
        where TResult : notnull
    {
        var started = Stopwatch.GetTimestamp();
        var budget = timeout ?? Defaults.WaitTimeout;
        var (snapshot, timedOut) = await AwaitExistingAsync(jobs, wakeup, job, budget, ct, ResultTypeCheck<TResult>(routing));
        while (true)
        {
            if (await OutcomeAsync<TResult>(jobs, routing, getJobResult, snapshot, timedOut, ct) is { } outcome)
            {
                return outcome;
            }

            var wait = await AwaitAsync(
                jobs,
                wakeup,
                snapshot.JobId,
                Remaining(budget, started),
                Defaults.PollInterval,
                ct,
                ResultTypeCheck<TResult>(routing)
            );
            (snapshot, timedOut) = (wait.Snapshot ?? throw Vanished(snapshot.JobRef), wait.TimedOut);
        }
    }

    // What is left of a wait's budget; at zero the wait reads once more and answers TimedOut unless the job is terminal.
    private static TimeSpan Remaining(TimeSpan budget, long started)
    {
        var remaining = budget - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static JobOutcome Outcome(GetJobResponse snapshot, bool timedOut) =>
        timedOut
            ? JobOutcome.TimedOut(snapshot.JobId, snapshot.JobRef, snapshot.Status)
            : snapshot.Status switch
            {
                JobStatusCode.Succeeded => JobOutcome.Succeeded(snapshot.JobId, snapshot.JobRef),
                JobStatusCode.Cancelled => JobOutcome.Cancelled(snapshot.JobId, snapshot.JobRef),
                _ => JobOutcome.Failed(snapshot.JobId, snapshot.JobRef),
            };

    /// <summary>
    /// The outcome, with the result stored by the execution the wait saw succeed, or null when that success is no longer
    /// the job's: an operator restart clears the stored result, so a read that finds none checks the job again, and a job
    /// that has moved on is waited for again. A success that is still the job's and stored none throws.
    /// </summary>
    private static async ValueTask<JobOutcome<TResult>?> OutcomeAsync<TResult>(
        IJobs jobs,
        JobRequestRouting routing,
        GetJobResultHandler getJobResult,
        GetJobResponse snapshot,
        bool timedOut,
        CancellationToken ct
    )
        where TResult : notnull
    {
        if (timedOut)
        {
            return JobOutcome<TResult>.TimedOut(snapshot.JobId, snapshot.JobRef, snapshot.Status);
        }

        switch (snapshot.Status)
        {
            case JobStatusCode.Succeeded:
                if (await getJobResult.ReadAsync(snapshot.JobId, snapshot.ExecutionNumber, ct) is { } result)
                {
                    return JobOutcome<TResult>.Succeeded(snapshot.JobId, snapshot.JobRef, routing.DecodeResult<TResult>(result)!);
                }

                var now = await jobs.GetAsync(JobLookup.ById(snapshot.JobId), ct) ?? throw Vanished(snapshot.JobRef);
                if (now.Status != JobStatusCode.Succeeded || now.ExecutionNumber != snapshot.ExecutionNumber)
                {
                    return null;
                }

                throw new InvalidOperationException(
                    $"Job {snapshot.JobRef} ('{snapshot.JobName}') succeeded but stored no result. Either it is "
                        + "result-less, in which case wait without a result type, or its result exceeded "
                        + "MaxInlinePayloadBytes and was dropped; the job's events say which."
                );
            case JobStatusCode.Cancelled:
                return JobOutcome<TResult>.Cancelled(snapshot.JobId, snapshot.JobRef);
            default:
                return JobOutcome<TResult>.Failed(snapshot.JobId, snapshot.JobRef);
        }
    }

    private static JobExecutionOptions Validated(JobExecutionOptions? options)
    {
        options ??= Defaults;
        if (options.WaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WaitTimeout, "WaitTimeout must be greater than zero.");
        }
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.PollInterval, "PollInterval must be greater than zero.");
        }
        return options;
    }

    /// <summary>
    /// The wait on a job that should exist: a lookup by id costs no resolve, a resolve spends the budget like the wait
    /// does, and the loop's first read is the existence check, so a job with no row throws without spending the rest.
    /// </summary>
    private static async ValueTask<(GetJobResponse Snapshot, bool TimedOut)> AwaitExistingAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobLookup job,
        TimeSpan? timeout,
        CancellationToken ct,
        Action<GetJobResponse>? check = null
    )
    {
        var budget = timeout ?? Defaults.WaitTimeout;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), budget, "The wait timeout must be greater than zero.");
        }

        var started = Stopwatch.GetTimestamp();
        var jobId = job.Kind == JobLookupKind.JobId ? job.JobId : (await jobs.GetIdAsync(job, ct))?.JobId;
        var remaining = budget - Stopwatch.GetElapsedTime(started);
        var (snapshot, timedOut) = jobId is { } id
            ? await AwaitAsync(jobs, wakeup, id, remaining, Defaults.PollInterval, ct, check)
            : default;
        return snapshot is not null
            ? (snapshot, timedOut)
            : throw new InvalidOperationException(
                $"No job matches {job}. A job enqueued in a transaction is invisible until that transaction commits."
            );
    }

    /// <summary>
    /// Between reads the wait sleeps on the job's completion channel, so a colocated or transported completion wake
    /// ends it at once; without one, the poll interval is the discovery floor.
    /// </summary>
    private static async ValueTask<(GetJobResponse? Snapshot, bool TimedOut)> AwaitAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        long jobId,
        TimeSpan waitTimeout,
        TimeSpan pollInterval,
        CancellationToken ct,
        Action<GetJobResponse>? check = null
    )
    {
        var job = JobLookup.ById(jobId);
        return await CompletionWait.AwaitAsync(
            wakeup,
            jobId,
            ReadAsync,
            static snapshot => snapshot.Status.IsTerminal,
            waitTimeout,
            pollInterval,
            ct
        );

        async ValueTask<GetJobResponse?> ReadAsync(CancellationToken c)
        {
            var snapshot = await jobs.GetAsync(job, c);
            if (snapshot is not null)
            {
                check?.Invoke(snapshot);
            }
            return snapshot;
        }
    }

    // Every read of a typed wait checks the result type, so a foreign definition's job fails on the first read, whatever
    // its outcome, instead of after the budget.
    private static Action<GetJobResponse> ResultTypeCheck<TResult>(JobRequestRouting routing) =>
        snapshot => routing.EnsureResultType<TResult>(snapshot.JobNamespace, snapshot.JobName);

    private static InvalidOperationException Vanished(JobRef jobRef) =>
        new($"Job {jobRef} no longer exists: retention or an operator purged it while the caller waited.");
}

/// <summary>
/// The wait loop: re-read the job until it is terminal, gone, or the wait budget runs out, sleeping on its completion
/// channel between reads. A read that finds no row ends the wait, since a deleted job does not come back. The next
/// sleep is registered before each read, so a completion that commits while the read is in flight still wakes it;
/// registered after the read, that wake would find no waiter, and a job channel keeps no latch without one.
/// </summary>
internal static class CompletionWait
{
    public static async ValueTask<(T? Snapshot, bool TimedOut)> AwaitAsync<T>(
        IWorkerWakeup wakeup,
        long jobId,
        Func<CancellationToken, ValueTask<T?>> read,
        Func<T, bool> isTerminal,
        TimeSpan waitTimeout,
        TimeSpan pollInterval,
        CancellationToken ct
    )
        where T : class
    {
        var channel = WorkerWakeupChannel.JobCompletion(jobId);
        var start = Stopwatch.GetTimestamp();

        while (true)
        {
            var remaining = waitTimeout - Stopwatch.GetElapsedTime(start);
            using var sleepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sleep =
                remaining > TimeSpan.Zero
                    ? wakeup.WaitAsync(channel, remaining < pollInterval ? remaining : pollInterval, sleepCts.Token).AsTask()
                    : null;

            T? snapshot;
            try
            {
                snapshot = await read(ct);
            }
            catch
            {
                await AbandonAsync(sleep, sleepCts);
                throw;
            }

            if (snapshot is null || isTerminal(snapshot))
            {
                await AbandonAsync(sleep, sleepCts);
                return (snapshot, false);
            }

            if (sleep is null)
            {
                return (snapshot, true);
            }

            await sleep;
        }
    }

    // Cancels a sleep the loop no longer needs and waits for it to retire its waiter.
    private static async ValueTask AbandonAsync(Task<WorkerWakeupWaitStatus>? sleep, CancellationTokenSource sleepCts)
    {
        if (sleep is null)
        {
            return;
        }

        await sleepCts.CancelAsync();
        try
        {
            await sleep;
        }
        catch (OperationCanceledException) when (sleepCts.IsCancellationRequested)
        {
            // The sleep was abandoned, not the caller's wait.
        }
    }
}
