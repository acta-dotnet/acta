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
    GetJobExplanationHandler getJobExplanation,
    GetJobLineageMapHandler getJobLineageMap,
    GetJobStatusHandler getJobStatus,
    GetJobInputHandler getJobInput,
    GetResultHandler getResult,
    GetCheckpointsHandler getCheckpoints,
    CancelJobHandler cancelJob,
    PauseJobHandler pauseJob,
    ResumeJobHandler resumeJob,
    RestartJobHandler restartJob,
    RescheduleJobHandler rescheduleJob,
    ReprioritizeJobHandler reprioritizeJob,
    UpdateJobInputHandler updateJobInput,
    PurgeJobHandler purgeJob,
    RaiseSignalHandler raiseSignal,
    GetInputTemplateHandler getInputTemplate,
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
        where TResult : notnull => JobRunAndWait.WaitAsync<TResult>(this, wakeup, routing, getResult, job, timeout, ct);

    public ValueTask<EnqueueJobsResponse> EnqueueBatchAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct = default) =>
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

    public ValueTask<EnqueueJobsResponse> EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct = default
    ) => enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => JobRunAndWait.RunAsync(this, wakeup, o => routing.FromInput(input, o), options, ct);

    public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        JobRunAndWait.RunAsync<TResult>(this, wakeup, routing, getResult, o => routing.FromInput(input, o), options, ct);

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
        JobRunAndWait.RunAsync<TResult>(this, wakeup, routing, getResult, o => routing.FromCall(job, o), options, ct);

    public ValueTask<GetJobIdResponse?> GetJobIdAsync(JobLookup job, CancellationToken ct = default) =>
        getJobId.HandleAsync(new GetJobIdQuery(job), ct);

    public ValueTask<GetJobResponse?> GetAsync(JobLookup job, CancellationToken ct = default) =>
        getJob.HandleAsync(new GetJobQuery(job), ct);

    public ValueTask<GetJobExplanationResponse?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
        getJobExplanation.HandleAsync(new GetJobExplanationQuery(job), ct);

    public ValueTask<GetJobLineageMapResponse?> GetLineageMapAsync(
        JobLookup job,
        GetJobLineageMapOptions? options = null,
        CancellationToken ct = default
    ) => getJobLineageMap.HandleAsync(new GetJobLineageMapQuery(job, options), ct);

    public ValueTask<GetJobStatusResponse?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
        getJobStatus.HandleAsync(new GetJobStatusQuery(job), ct);

    public ValueTask<GetJobInputResponse?> GetInputAsync(JobLookup job, CancellationToken ct = default) =>
        getJobInput.HandleAsync(new GetJobInputQuery(job), ct);

    public ValueTask<GetResultResponse?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
        getResult.HandleAsync(new GetResultQuery(job), ct);

    public async ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
        await GetResultAsync(job, ct) is { } response ? routing.Decode<TResult>(response.Result) : default;

    /// <summary>The result <paramref name="executionNumber"/> stored, or the latest execution's when null; null when there is none.</summary>
    internal Task<GetResultRow?> ReadResultAsync(long jobId, int? executionNumber, CancellationToken ct) =>
        getResult.ReadAsync(jobId, executionNumber, ct);

    public ValueTask<GetCheckpointsResponse?> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
        getCheckpoints.HandleAsync(new GetCheckpointsQuery(job), ct);

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

    public ValueTask<UpdateJobInputResponse> UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => updateJobInput.HandleAsync(new UpdateJobInputCommand(job, input, reasonMessage, actor), ct);

    public ValueTask<PurgeJobResponse> PurgeAsync(JobLookup job, string? actor = null, CancellationToken ct = default) =>
        purgeJob.HandleAsync(new PurgeJobCommand(job, actor), ct);

    public ValueTask<RaiseSignalResponse> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actor = null,
        CancellationToken ct = default
    ) => RaiseSignalAsync(job, name, routing.Json(value), actor, ct);

    public ValueTask<RaiseSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actor = null,
        CancellationToken ct = default
    ) => raiseSignal.HandleAsync(new RaiseSignalCommand(job, name, value, actor), ct);

    public GetInputTemplateResponse? GetInputTemplate(string jobNamespace, string jobName) =>
        getInputTemplate.Handle(new GetInputTemplateQuery(jobNamespace, jobName));

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
