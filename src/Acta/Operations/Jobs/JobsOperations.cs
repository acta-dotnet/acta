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
    GetJobCheckpointValueHandler getJobCheckpointValue,
    GetJobVariableOrDefaultHandler getJobVariableOrDefault,
    GetJobStepResultOrDefaultHandler getJobStepResultOrDefault,
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
    ClearJobSignalHandler clearJobSignal,
    ResetJobStateHandler resetJobState,
    ListJobsByRefHandler listJobsByRef
) : IJobs
{
    public Task<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request), ct);

    public Task<JobHandle> EnqueueAsync<TInput>(
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => HandleAsync(routing.FromInput(input, Options(configure)), ct);

    public Task<JobHandle> EnqueueAsync<TInput>(
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => HandleAsync(routing.FromCall(job, Options(configure)), ct);

    public Task<JobHandle<TResult>> EnqueueAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => HandleAsync<TResult>(routing.FromCall(job, Options(configure)), ct);

    public Task<JobOutcome> WaitAsync(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default) =>
        JobRunAndWait.WaitAsync(this, wakeup, job, timeout, ct);

    public Task<JobOutcome<TResult>> WaitAsync<TResult>(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default)
        where TResult : notnull => JobRunAndWait.WaitAsync<TResult>(this, wakeup, routing, getJobResult, job, timeout, ct);

    public Task<EnqueueJobsResponse> EnqueueJobsAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct = default) =>
        enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests), ct);

    public Task<EnqueueJobResponse> EnqueueAsync(DbTransaction transaction, EnqueueJobRequest request, CancellationToken ct = default) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public Task<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromInput(input, Options(configure)), ct);

    public Task<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromCall(job, Options(configure)), ct);

    public Task<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => EnqueueAsync(transaction, routing.FromCall(job, Options(configure)), ct);

    public Task<EnqueueJobsResponse> EnqueueJobsAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct = default
    ) => enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public Task<JobOutcome> CallAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => JobRunAndWait.RunAsync(this, wakeup, o => routing.FromInput(input, o), options, ct);

    public Task<JobOutcome> CallAsync<TInput>(JobCall<TInput> job, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => JobRunAndWait.RunAsync(this, wakeup, o => routing.FromCall(job, o), options, ct);

    public Task<JobOutcome<TResult>> CallAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        JobRunAndWait.RunAsync<TResult>(this, wakeup, routing, getJobResult, o => routing.FromCall(job, o), options, ct);

    public Task<GetJobIdResponse?> GetIdAsync(JobLookup job, CancellationToken ct = default) =>
        getJobId.HandleAsync(new GetJobIdQuery(job), ct);

    public Task<GetJobResponse?> GetAsync(JobLookup job, CancellationToken ct = default) => getJob.HandleAsync(new GetJobQuery(job), ct);

    public Task<ExplainJobResponse?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
        explainJob.HandleAsync(new ExplainJobQuery(job), ct);

    public Task<GetJobLineageMapResponse?> GetLineageMapAsync(
        JobLookup job,
        GetJobLineageMapOptions? options = null,
        CancellationToken ct = default
    ) => getJobLineageMap.HandleAsync(new GetJobLineageMapQuery(job, options), ct);

    public Task<GetJobStatusResponse?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
        getJobStatus.HandleAsync(new GetJobStatusQuery(job), ct);

    public Task<GetJobInputResponse?> GetInputAsync(JobLookup job, CancellationToken ct = default) =>
        getJobInput.HandleAsync(new GetJobInputQuery(job), ct);

    public async Task<TInput> GetInputAsync<TInput>(JobLookup job, CancellationToken ct = default)
        where TInput : notnull =>
        await getJobInput.ReadAsync(job, ct) is { } row
            ? routing.DecodeInput<TInput>(row)
            : throw new InvalidOperationException(job.NoJobMessage());

    public Task<GetJobResultResponse?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
        getJobResult.HandleAsync(new GetJobResultQuery(job), ct);

    public async Task<(bool Found, TResult? Value)> TryGetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
        routing.TryDecodeResult<TResult>(await getJobResult.ReadAsync(job, ct));

    /// <summary>The result <paramref name="executionNumber"/> stored, or the latest execution's when null, read as <typeparamref name="TResult"/>.</summary>
    internal async Task<(bool Stored, TResult? Value)> ReadResultAsync<TResult>(long jobId, long? executionNumber, CancellationToken ct) =>
        routing.TryDecodeResult<TResult>(await getJobResult.ReadAsync(jobId, executionNumber, ct));

    /// <summary>
    /// The success of the job's execution <paramref name="executionNumber"/>, or of its current one when null, with the
    /// result it stored or none, beside the job as last read; no success when the job has moved on since.
    /// </summary>
    internal ValueTask<(JobOutcome<TResult>? Success, GetJobResponse? Now)> ReadSuccessAsync<TResult>(
        long jobId,
        JobRef jobRef,
        long? executionNumber,
        CancellationToken ct
    )
        where TResult : notnull => JobRunAndWait.SuccessAsync<TResult>(this, routing, getJobResult, jobId, jobRef, executionNumber, ct);

    public Task<ListJobCheckpointsResponse> ListCheckpointsAsync(ListJobCheckpointsQuery query, CancellationToken ct = default) =>
        listJobCheckpoints.HandleAsync(query, ct);

    public Task<GetJobCheckpointValueResponse?> GetCheckpointValueAsync(
        JobLookup job,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct = default
    ) => getJobCheckpointValue.HandleAsync(new GetJobCheckpointValueQuery(job, kind, name), ct);

    public async Task<T?> GetVariableOrDefaultAsync<T>(JobLookup job, string name, CancellationToken ct = default) =>
        await getJobVariableOrDefault.HandleAsync(new GetJobVariableOrDefaultQuery(job, name), ct) is { } variable
            ? routing.Decode<T>(variable.Value)
            : default;

    public async Task<TResult?> GetStepResultOrDefaultAsync<TResult>(JobLookup job, string name, CancellationToken ct = default) =>
        await getJobStepResultOrDefault.HandleAsync(new GetJobStepResultOrDefaultQuery(job, name), ct) is { } step
            ? routing.Decode<TResult>(step.Value)
            : default;

    public Task<ListJobStepsResponse> ListStepsAsync(ListJobStepsQuery query, CancellationToken ct = default) =>
        listJobSteps.HandleAsync(query, ct);

    public Task<CancelJobResponse> CancelAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => cancelJob.HandleAsync(new CancelJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public Task<PauseJobResponse> PauseAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => pauseJob.HandleAsync(new PauseJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public Task<ResumeJobResponse> ResumeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => resumeJob.HandleAsync(new ResumeJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public Task<RestartJobResponse> RestartAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => restartJob.HandleAsync(new RestartJobCommand(job, reasonMessage, actor, expectedVersion), ct);

    public Task<RescheduleJobResponse> RescheduleAsync(
        JobLookup job,
        DateTimeOffset? nextRunAtUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => rescheduleJob.HandleAsync(new RescheduleJobCommand(job, nextRunAtUtc?.UtcDateTime, reasonMessage, actor, expectedVersion), ct);

    public Task<ReprioritizeJobResponse> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => reprioritizeJob.HandleAsync(new ReprioritizeJobCommand(job, priority, reasonMessage, actor, expectedVersion), ct);

    public Task<UpdateJobInputResponse> UpdateInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actor = null,
        long? expectedVersion = null,
        CancellationToken ct = default
    ) => updateJobInput.HandleAsync(new UpdateJobInputCommand(job, input, reasonMessage, actor, expectedVersion), ct);

    public Task<PurgeJobResponse> PurgeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => purgeJob.HandleAsync(new PurgeJobCommand(job, reasonMessage, actor), ct);

    public Task<RaiseJobSignalResponse> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actor = null,
        CancellationToken ct = default
    ) => RaiseSignalAsync(job, name, routing.Json(value), actor, ct);

    public Task<RaiseJobSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actor = null,
        CancellationToken ct = default
    ) => raiseJobSignal.HandleAsync(new RaiseJobSignalCommand(job, name, value, actor), ct);

    public Task<ClearJobSignalResponse> ClearSignalAsync(
        JobLookup job,
        string name,
        string? actor = null,
        CancellationToken ct = default
    ) => clearJobSignal.HandleAsync(new ClearJobSignalCommand(job, name, actor), ct);

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
    internal Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(
        TInput input,
        JobEnqueueOptions options,
        ExecutionFence parent,
        CancellationToken ct
    )
        where TInput : notnull => EnqueueChildAsync(routing.FromInput(input, options), parent, ct);

    /// <summary>A child enqueue of a generated call, by the job the call names, under its parent's fence like the typed one.</summary>
    internal Task<EnqueueJobResponse> EnqueueChildAsync<TInput>(
        Type? manifestType,
        string? jobName,
        TInput input,
        Type? resultType,
        JobEnqueueOptions options,
        ExecutionFence parent,
        CancellationToken ct
    )
        where TInput : notnull => EnqueueChildAsync(routing.FromContract(manifestType, jobName, input, options, resultType), parent, ct);

    /// <summary>A child enqueue of an explicit route, under its parent's fence like the typed one.</summary>
    internal Task<EnqueueJobResponse> EnqueueChildAsync(EnqueueJobRequest request, ExecutionFence parent, CancellationToken ct) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request, ParentFence: parent), ct);

    /// <summary>The batch enqueue under the ref given at each request's position instead of a fresh one.</summary>
    internal Task<EnqueueJobsResponse> EnqueueJobsAsync(
        IReadOnlyList<EnqueueJobRequest> requests,
        IReadOnlyList<Guid> jobRefs,
        CancellationToken ct
    ) => enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests, JobRefs: jobRefs), ct);

    internal Task<ListJobsByRefResponse> ListJobsByRefAsync(IReadOnlyList<Guid> jobRefs, CancellationToken ct) =>
        listJobsByRef.HandleAsync(new ListJobsByRefQuery(jobRefs), ct);

    // A typed enqueue's handle carries the committed id, so each of its verbs is one round trip.
    private async Task<JobHandle> HandleAsync(EnqueueJobRequest request, CancellationToken ct)
    {
        var enqueued = await EnqueueAsync(request, ct);
        return new JobHandle(this, enqueued.JobId, enqueued.JobRef, enqueued.Action);
    }

    private async Task<JobHandle<TResult>> HandleAsync<TResult>(EnqueueJobRequest request, CancellationToken ct)
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
