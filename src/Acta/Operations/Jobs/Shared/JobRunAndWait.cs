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

    /// <summary>The same wait, then the result; a succeeded job that stored none throws rather than answering a default.</summary>
    public static async ValueTask<JobOutcome<TResult>> RunAsync<TResult>(
        IJobs jobs,
        IWorkerWakeup wakeup,
        JobRequestRouting routing,
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
                    await jobs.GetResultAsync(JobLookup.ById(jobId), ct)
                    ?? throw new InvalidOperationException(
                        $"Job {jobId} ('{snapshot.JobName}') succeeded but stored no result. Either it is "
                            + "result-less, in which case use the non-result RunAndWaitAsync overload, or its "
                            + "result exceeded MaxInlinePayloadBytes and was dropped; the job's events say which."
                    );
                return JobOutcome<TResult>.Succeeded(jobId, routing.Decode<TResult>(result.Result)!);
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
