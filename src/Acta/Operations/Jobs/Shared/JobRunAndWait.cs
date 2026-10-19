using System.Diagnostics;

namespace Acta;

/// <summary>
/// <see cref="IJobs.RunAndWaitAsync{TInput}"/> over the public operations: validates the wait options before anything
/// is enqueued, enqueues, then waits client-side for a terminal status. The budget is local monotonic time, not the
/// Acta clock: the job keeps running after the caller stops waiting.
/// </summary>
internal static class JobRunAndWait
{
    public static async ValueTask<JobOutcome> RunAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
    {
        var (jobId, snapshot, timedOut) = await EnqueueAndAwaitAsync(jobs, wakeup, request, options, ct);
        return timedOut
            ? JobOutcome.TimedOut(jobId, snapshot?.Status ?? JobStatusCode.Ready)
            : snapshot!.Status switch
            {
                JobStatusCode.Succeeded => JobOutcome.Succeeded(jobId),
                JobStatusCode.Cancelled => JobOutcome.Cancelled(jobId),
                _ => JobOutcome.Failed(jobId),
            };
    }

    /// <summary>
    /// The same wait, then the result stored by the execution the wait saw succeed. A restarted job keeps its earlier
    /// executions' results, so an execution that stored none throws rather than answering an older one or a default.
    /// </summary>
    public static async ValueTask<JobOutcome<TResult>> RunAsync<TResult>(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobRequestRouting routing,
        GetResultHandler getResult,
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
        where TResult : notnull
    {
        var (jobId, snapshot, timedOut) = await EnqueueAndAwaitAsync(jobs, wakeup, request, options, ct);
        if (timedOut)
        {
            return JobOutcome<TResult>.TimedOut(jobId, snapshot?.Status ?? JobStatusCode.Ready);
        }

        switch (snapshot!.Status)
        {
            case JobStatusCode.Succeeded:
                var result =
                    await getResult.ReadAsync(jobId, snapshot.ExecutionNumber, ct)
                    ?? throw new InvalidOperationException(
                        $"Job {jobId} ('{snapshot.JobName}') succeeded but stored no result. Either it is "
                            + "result-less, in which case use the non-result RunAndWaitAsync overload, or its "
                            + "result exceeded MaxInlinePayloadBytes and was dropped; the job's events say which."
                    );
                return JobOutcome<TResult>.Succeeded(
                    jobId,
                    routing.Decode<TResult>(JobPayload.FromBytes(result.Format, result.Data.ToArray()))!
                );
            case JobStatusCode.Cancelled:
                return JobOutcome<TResult>.Cancelled(jobId);
            default:
                return JobOutcome<TResult>.Failed(jobId);
        }
    }

    /// <summary>
    /// Between reads the wait sleeps on the job's completion channel, so a colocated or transported completion wake
    /// ends it at once; without one, PollInterval is the discovery floor.
    /// </summary>
    private static async ValueTask<(long JobId, GetJobResponse? Snapshot, bool TimedOut)> EnqueueAndAwaitAsync(
        IJobs jobs,
        IWorkerWakeup wakeup,
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
    {
        options ??= new JobExecutionOptions();
        if (options.WaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WaitTimeout, "WaitTimeout must be greater than zero.");
        }
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.PollInterval, "PollInterval must be greater than zero.");
        }

        var jobId = (await jobs.EnqueueAsync(request(options), ct)).JobId;
        var job = JobLookup.ById(jobId);
        var (snapshot, timedOut) = await CompletionWait.AwaitAsync(
            wakeup,
            jobId,
            c => jobs.GetAsync(job, c),
            static snapshot => snapshot.Status.IsTerminal,
            options.WaitTimeout,
            options.PollInterval,
            ct
        );
        return (jobId, snapshot, timedOut);
    }
}

/// <summary>
/// The enqueue-and-wait loop: re-read the job until it is terminal or the wait budget runs out, sleeping
/// on its completion channel between reads. The next sleep is registered before each read, so a
/// completion that commits while the read is in flight still wakes it; registered after the read, that
/// wake would find no waiter, and a job channel keeps no latch without one.
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
        T? last = null;

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

            if (snapshot is not null)
            {
                last = snapshot;
                if (isTerminal(snapshot))
                {
                    await AbandonAsync(sleep, sleepCts);
                    return (snapshot, false);
                }
            }

            if (sleep is null)
            {
                return (last, true);
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
