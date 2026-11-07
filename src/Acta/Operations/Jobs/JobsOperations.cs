using System.Data.Common;

namespace Acta;

/// <summary>
/// The Jobs operations: <see cref="IJobs"/> for applications, plus the engine's calls. Typed overloads build their request
/// through <see cref="JobRequestRouting"/>.
/// </summary>
internal sealed class JobsOperations(
    JobRequestRouting routing,
    EnqueueJobHandler enqueueJob,
    EnqueueJobsHandler enqueueJobs,
    IWorkerWakeup wakeup,
    GetJobIdHandler getJobId,
    GetJobHandler getJob,
    ExplainJobHandler explainJob,
    GetJobLineageMapHandler getJobLineageMap,
    GetJobStatusHandler getJobStatus,
    GetJobInputHandler getJobInput,
    GetJobResultHandler getJobResult,
    ListJobCheckpointsHandler listJobCheckpoints,
    ListJobStepsHandler listJobSteps,
    CancelJobHandler cancelJob,
    PauseJobHandler pauseJob,
    ResumeJobHandler resumeJob,
    RestartJobHandler restartJob,
    RescheduleJobHandler rescheduleJob,
    ReprioritizeJobHandler reprioritizeJob,
    UpdateJobInputHandler updateJobInput,
    PurgeJobHandler purgeJob,
    RaiseJobSignalHandler raiseJobSignal,
    ResetJobStateHandler resetJobState
) : IJobs
{
    public ValueTask<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request), ct);

    public ValueTask<JobHandle> EnqueueAsync<TInput>(TInput input, JobEnqueueOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => HandleAsync(routing.FromInput(input, options), ct);

    public ValueTask<JobHandle> EnqueueAsync<TInput>(
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => HandleAsync(routing.FromCall(job, Options(configure)), ct);

    public ValueTask<JobHandle<TResult>> EnqueueAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => HandleAsync<TResult>(routing.FromCall(job, Options(configure)), ct);

    public ValueTask<JobOutcome> WaitAsync(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default) =>
        JobRunAndWait.WaitAsync(this, wakeup, job, timeout, ct);

    public ValueTask<JobOutcome<TResult>> WaitAsync<TResult>(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default)
        where TResult : notnull => JobRunAndWait.WaitAsync<TResult>(this, wakeup, routing, getJobResult, job, timeout, ct);

    public ValueTask<EnqueueJobsResponse> EnqueueJobsAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct = default) =>
        enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync(
        DbTransaction transaction,
        EnqueueJobRequest request,
        CancellationToken ct = default
    ) => enqueueJob.HandleAsync(new EnqueueJobCommand(request, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromInput(input, options), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromCall(job, Options(configure)), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => EnqueueAsync(transaction, routing.FromCall(job, Options(configure)), ct);

    public ValueTask<EnqueueJobsResponse> EnqueueJobsAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct = default
    ) => enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => JobRunAndWait.RunAsync(this, wakeup, o => routing.FromInput(input, o), options, ct);

    public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
        JobCall<TInput> job,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => JobRunAndWait.RunAsync(this, wakeup, o => routing.FromCall(job, o), options, ct);

    public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        JobRunAndWait.RunAsync<TResult>(this, wakeup, routing, getJobResult, o => routing.FromCall(job, o), options, ct);

    public ValueTask<GetJobIdResponse?> GetIdAsync(JobLookup job, CancellationToken ct = default) =>
        getJobId.HandleAsync(new GetJobIdQuery(job), ct);

    public ValueTask<GetJobResponse?> GetAsync(JobLookup job, CancellationToken ct = default) =>
        getJob.HandleAsync(new GetJobQuery(job), ct);

    public ValueTask<ExplainJobResponse?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
        explainJob.HandleAsync(new ExplainJobQuery(job), ct);

    public ValueTask<GetJobLineageMapResponse?> GetLineageMapAsync(
        JobLookup job,
        GetJobLineageMapOptions? options = null,
        CancellationToken ct = default
    ) => getJobLineageMap.HandleAsync(new GetJobLineageMapQuery(job, options), ct);

    public ValueTask<GetJobStatusResponse?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
        getJobStatus.HandleAsync(new GetJobStatusQuery(job), ct);

    public ValueTask<GetJobInputResponse?> GetInputAsync(JobLookup job, CancellationToken ct = default) =>
        getJobInput.HandleAsync(new GetJobInputQuery(job), ct);

    public ValueTask<GetJobResultResponse?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
        getJobResult.HandleAsync(new GetJobResultQuery(job), ct);

    public async ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
        await getJobResult.ReadAsync(job, ct) is { } row ? routing.DecodeResult<TResult>(row) : default;

    /// <summary>The result <paramref name="executionNumber"/> stored, or the latest execution's when null, read as <typeparamref name="TResult"/>.</summary>
    internal async Task<(bool Stored, TResult? Value)> ReadResultAsync<TResult>(long jobId, int? executionNumber, CancellationToken ct) =>
        await getJobResult.ReadAsync(jobId, executionNumber, ct) is { } row ? (true, routing.DecodeResult<TResult>(row)) : (false, default);

    public ValueTask<ListJobCheckpointsResponse> ListCheckpointsAsync(ListJobCheckpointsQuery query, CancellationToken ct = default) =>
        listJobCheckpoints.HandleAsync(query, ct);

    public ValueTask<ListJobStepsResponse> ListStepsAsync(ListJobStepsQuery query, CancellationToken ct = default) =>
        listJobSteps.HandleAsync(query, ct);

    public ValueTask<CancelJobResponse> CancelAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => cancelJob.HandleAsync(new CancelJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<PauseJobResponse> PauseAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => pauseJob.HandleAsync(new PauseJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<ResumeJobResponse> ResumeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => resumeJob.HandleAsync(new ResumeJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<RestartJobResponse> RestartAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => restartJob.HandleAsync(new RestartJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<RescheduleJobResponse> RescheduleAsync(
        JobLookup job,
        DateTime? nextRunAtUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => rescheduleJob.HandleAsync(new RescheduleJobCommand(job, nextRunAtUtc, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<ReprioritizeJobResponse> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => reprioritizeJob.HandleAsync(new ReprioritizeJobCommand(job, priority, reasonMessage, actor, expectedVersion), ct);

    public ValueTask<UpdateJobInputResponse> UpdateInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => updateJobInput.HandleAsync(new UpdateJobInputCommand(job, input, reasonMessage, actor), ct);

    public ValueTask<PurgeJobResponse> PurgeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => purgeJob.HandleAsync(new PurgeJobCommand(job, reasonMessage, actor), ct);

    public ValueTask<RaiseJobSignalResponse> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actor = null,
        CancellationToken ct = default
    ) => RaiseSignalAsync(job, name, routing.Json(value), actor, ct);

    public ValueTask<RaiseJobSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actor = null,
        CancellationToken ct = default
    ) => raiseJobSignal.HandleAsync(new RaiseJobSignalCommand(job, name, value, actor), ct);

    /// <summary>
    /// Cancels the non-terminal descendants of a job its own handler cancelled, stamped as an ancestor's cancellation by
    /// the system actor. Wakes every worker namespace once if a cancel promoted a lane member.
    /// </summary>
    internal Task CancelDescendantsAsync(long jobId, CancellationToken ct) =>
        cancelJob.CancelDescendantsAsync(jobId, CancelJobHandler.ParentCancelled, ct);

    /// <summary>
    /// Cancels a job and then its descendants under one input, waking every worker namespace once if any cancel
    /// promoted a lane member: a parent abandoning a child whose bounded wait expired, and only while
    /// <paramref name="parent"/> holds the parent.
    /// </summary>
    internal Task CancelTreeAsync(long rootJobId, JobControlInput input, ExecutionFence parent, CancellationToken ct) =>
        cancelJob.CancelTreeAsync(rootJobId, input, parent, ct);

    /// <summary>
    /// Clears a job's durable step and checkpoint state ahead of a fresh attempt, behind <c>ctx.ResetStateAsync</c>, while
    /// <paramref name="fence"/> holds the job.
    /// </summary>
    internal Task ResetJobStateAsync(ExecutionFence fence, CancellationToken ct) =>
        resetJobState.HandleAsync(new ResetJobStateCommand(fence), ct);

    /// <summary>
    /// Enqueues a child from its parent's running handler, and only while <paramref name="parent"/> holds the parent, so
    /// an attempt that lost the parent starts no child.
    /// </summary>
    internal ValueTask<EnqueueJobResponse> EnqueueChildAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        ExecutionFence parent,
        CancellationToken ct
    )
        where TInput : notnull => EnqueueChildAsync(routing.FromInput(input, options), parent, ct);

    /// <summary>A child enqueue of an explicit route, under its parent's fence like the typed one.</summary>
    internal ValueTask<EnqueueJobResponse> EnqueueChildAsync(EnqueueJobRequest request, ExecutionFence parent, CancellationToken ct) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request, ParentFence: parent), ct);

    // A typed enqueue's handle carries the committed id, so each of its verbs is one round trip.
    private async ValueTask<JobHandle> HandleAsync(EnqueueJobRequest request, CancellationToken ct)
    {
        var enqueued = await EnqueueAsync(request, ct);
        return new JobHandle(this, enqueued.JobId, enqueued.JobRef, enqueued.Action);
    }

    private async ValueTask<JobHandle<TResult>> HandleAsync<TResult>(EnqueueJobRequest request, CancellationToken ct)
        where TResult : notnull
    {
        var enqueued = await EnqueueAsync(request, ct);
        return new JobHandle<TResult>(this, enqueued.JobId, enqueued.JobRef, enqueued.Action);
    }

    private static JobEnqueueOptions? Options(Action<JobEnqueueOptionsBuilder>? configure)
    {
        if (configure is null)
        {
            return null;
        }
        var builder = new JobEnqueueOptionsBuilder();
        configure(builder);
        return builder.Build();
    }
}
