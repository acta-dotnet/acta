namespace Acta;

/// <summary>
/// <see cref="IJobs.RunAndWaitAsync{TInput}"/>: validates the wait options before anything is enqueued, enqueues,
/// then waits client-side for a terminal status. The budget is local monotonic time, not the Acta clock: the job
/// keeps running after the caller stops waiting.
/// </summary>
internal sealed class JobRunAndWait(
    EnqueueJobHandler enqueueJob,
    GetJobHandler getJob,
    GetResultHandler getResult,
    IJobPayloadSerializerRegistry serializers,
    IWorkerWakeup wakeup
)
{
    public async ValueTask<JobOutcome> RunAsync(
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
    {
        var (jobId, snapshot, timedOut) = await EnqueueAndAwaitAsync(request, options, ct);
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
    public async ValueTask<JobOutcome<TResult>> RunAsync<TResult>(
        Func<JobExecutionOptions, EnqueueJobRequest> request,
        JobExecutionOptions? options,
        CancellationToken ct
    )
        where TResult : notnull
    {
        var (jobId, snapshot, timedOut) = await EnqueueAndAwaitAsync(request, options, ct);
        if (timedOut)
        {
            return JobOutcome<TResult>.TimedOut(jobId, snapshot?.Status ?? JobStatusCode.Ready);
        }

        switch (snapshot!.Status)
        {
            case JobStatusCode.Succeeded:
                var result =
                    await getResult.ReadAsync(jobId, executionNumber: null, ct)
                    ?? throw new InvalidOperationException(
                        $"Job {jobId} ('{snapshot.JobName}') succeeded but stored no result. Either it is "
                            + "result-less, in which case use the non-result RunAndWaitAsync overload, or its "
                            + "result exceeded MaxInlinePayloadBytes and was dropped; the job's events say which."
                    );
                var payload = JobPayload.FromBytes(result.Format, result.Data.ToArray());
                return JobOutcome<TResult>.Succeeded(jobId, serializers.Resolve(payload.Format.Id).Deserialize<TResult>(payload)!);
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
    private async ValueTask<(long JobId, GetJobResponse? Snapshot, bool TimedOut)> EnqueueAndAwaitAsync(
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

        var jobId = (await enqueueJob.HandleAsync(new EnqueueJobCommand(request(options)), ct)).JobId;
        var (snapshot, timedOut) = await CompletionWait.AwaitAsync(
            wakeup,
            jobId,
            c => getJob.ReadAsync(jobId, c),
            static snapshot => snapshot.Status.IsTerminal,
            options.WaitTimeout,
            options.PollInterval,
            ct
        );
        return (jobId, snapshot, timedOut);
    }
}
