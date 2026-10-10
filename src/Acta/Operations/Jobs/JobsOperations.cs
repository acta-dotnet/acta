using System.Data.Common;

namespace Acta;

/// <summary><see cref="IJobsOperations"/>, one line per operation; typed overloads build their request through <see cref="JobRequestRouting"/>.</summary>
internal sealed class JobsOperations(
    JobRequestRouting routing,
    EnqueueJobHandler enqueueJob,
    EnqueueJobsHandler enqueueJobs,
    JobRunAndWait runAndWait,
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
) : IJobsOperations
{
    public ValueTask<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default) =>
        enqueueJob.HandleAsync(new EnqueueJobCommand(request), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(routing.FromInput(input, options), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(routing.FromContract(job, input, options), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync((JobContract<TInput>)job, input, options, ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync(
        JobContract<NoInput> job,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    ) => EnqueueAsync(routing.FromContract(job, default, options), ct);

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
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromContract(job, input, options), ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, (JobContract<TInput>)job, input, options, ct);

    public ValueTask<EnqueueJobResponse> EnqueueAsync(
        DbTransaction transaction,
        JobContract<NoInput> job,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    ) => EnqueueAsync(transaction, routing.FromContract(job, default, options), ct);

    public ValueTask<EnqueueJobsResponse> EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct = default
    ) => enqueueJobs.HandleAsync(new EnqueueJobsCommand(requests, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull => runAndWait.RunAsync(o => routing.FromInput(input, o), options, ct);

    public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull => runAndWait.RunAsync<TResult>(o => routing.FromInput(input, o), options, ct);

    public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull =>
        runAndWait.RunAsync<TResult>(o => routing.FromContract((JobContract<TInput>)job, input, o, typeof(TResult)), options, ct);

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

    public ValueTask<GetCheckpointsResponse?> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
        getCheckpoints.HandleAsync(new GetCheckpointsQuery(job), ct);

    public ValueTask<CancelJobResponse> CancelAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => cancelJob.HandleAsync(new CancelJobCommand(job, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<PauseJobResponse> PauseAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => pauseJob.HandleAsync(new PauseJobCommand(job, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<ResumeJobResponse> ResumeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => resumeJob.HandleAsync(new ResumeJobCommand(job, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<RestartJobResponse> RestartAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => restartJob.HandleAsync(new RestartJobCommand(job, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<RescheduleJobResponse> RescheduleAsync(
        JobLookup job,
        DateTime nextRunAtUtc,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => rescheduleJob.HandleAsync(new RescheduleJobCommand(job, nextRunAtUtc, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<ReprioritizeJobResponse> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actorKey = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    ) => reprioritizeJob.HandleAsync(new ReprioritizeJobCommand(job, priority, reasonMessage, actorKey, expectedVersion), ct);

    public ValueTask<UpdateJobInputResponse> UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => updateJobInput.HandleAsync(new UpdateJobInputCommand(job, input, reasonMessage, actorKey), ct);

    public ValueTask<PurgeJobResponse> PurgeAsync(JobLookup job, string? actorKey = null, CancellationToken ct = default) =>
        purgeJob.HandleAsync(new PurgeJobCommand(job, actorKey), ct);

    public ValueTask<RaiseSignalResponse> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actorKey = null,
        CancellationToken ct = default
    ) => RaiseSignalAsync(job, name, routing.Json(value), actorKey, ct);

    public ValueTask<RaiseSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actorKey = null,
        CancellationToken ct = default
    ) => raiseSignal.HandleAsync(new RaiseSignalCommand(job, name, value, actorKey), ct);

    public GetInputTemplateResponse? GetInputTemplate(string jobNamespace, string jobName) =>
        getInputTemplate.Handle(new GetInputTemplateQuery(jobNamespace, jobName));

    public Task CancelDescendantsAsync(long jobId, CancellationToken ct) =>
        cancelJob.CancelDescendantsAsync(jobId, CancelJobHandler.ParentCancelled, ct);

    public Task CancelTreeAsync(long rootJobId, JobControlInput input, CancellationToken ct) =>
        cancelJob.CancelTreeAsync(rootJobId, input, ct);

    public Task ResetJobStateAsync(long jobId, CancellationToken ct) => resetJobState.HandleAsync(new ResetJobStateCommand(jobId), ct);
}
