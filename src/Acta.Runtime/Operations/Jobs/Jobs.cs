using System.Data.Common;

namespace Acta.Runtime;

/// <summary><see cref="IJobs"/>, one line per operation; typed overloads build their request through <see cref="JobRequestRouting"/>.</summary>
internal sealed class Jobs(
    JobRequestRouting routing,
    EnqueueOneHandler enqueueOne,
    EnqueueBatchHandler enqueueBatch,
    JobRunAndWait runAndWait,
    JobLookupResolution lookup,
    GetJobHandler getJob,
    GetJobExplanationHandler getJobExplanation,
    GetJobLineageMapHandler getJobLineageMap,
    GetJobStatusHandler getJobStatus,
    GetJobInputHandler getJobInput,
    GetJobResultHandler getJobResult,
    GetJobCheckpointsHandler getJobCheckpoints,
    CancelJobHandler cancelJob,
    PauseJobHandler pauseJob,
    ResumeJobHandler resumeJob,
    RestartJobHandler restartJob,
    RescheduleJobHandler rescheduleJob,
    ReprioritizeJobHandler reprioritizeJob,
    UpdateJobInputHandler updateJobInput,
    PurgeJobHandler purgeJob,
    RaiseSignalHandler raiseSignal,
    GetInputTemplateHandler getInputTemplate
) : IJobs
{
    public ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct = default) =>
        enqueueOne.HandleAsync(new EnqueueOneCommand(request), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(routing.FromInput(input, options), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(routing.FromContract(job, input, options), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync((JobContract<TInput>)job, input, options, ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync(
        JobContract<NoInput> job,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    ) => EnqueueAsync(routing.FromContract(job, default, options), ct);

    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct = default
    ) => enqueueBatch.HandleAsync(new EnqueueBatchCommand(requests), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync(
        DbTransaction transaction,
        JobEnqueueRequest request,
        CancellationToken ct = default
    ) => enqueueOne.HandleAsync(new EnqueueOneCommand(request, transaction ?? throw new ArgumentNullException(nameof(transaction))), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromInput(input, options), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, routing.FromContract(job, input, options), ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull => EnqueueAsync(transaction, (JobContract<TInput>)job, input, options, ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync(
        DbTransaction transaction,
        JobContract<NoInput> job,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    ) => EnqueueAsync(transaction, routing.FromContract(job, default, options), ct);

    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct = default
    ) =>
        enqueueBatch.HandleAsync(
            new EnqueueBatchCommand(requests, transaction ?? throw new ArgumentNullException(nameof(transaction))),
            ct
        );

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

    public ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct = default) => lookup.ResolveAsync(job, ct);

    public ValueTask<JobDetail?> GetAsync(JobLookup job, CancellationToken ct = default) => getJob.HandleAsync(new GetJobQuery(job), ct);

    public ValueTask<JobExplanation?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
        getJobExplanation.HandleAsync(new GetJobExplanationQuery(job), ct);

    public ValueTask<JobLineageMap?> GetLineageMapAsync(
        JobLookup job,
        JobLineageMapOptions? options = null,
        CancellationToken ct = default
    ) => getJobLineageMap.HandleAsync(new GetJobLineageMapQuery(job, options), ct);

    public ValueTask<JobStatusCode?> GetStatusAsync(JobLookup job, CancellationToken ct = default) =>
        getJobStatus.HandleAsync(new GetJobStatusQuery(job), ct);

    public ValueTask<JobPayload?> GetInputAsync(JobLookup job, CancellationToken ct = default) =>
        getJobInput.HandleAsync(new GetJobInputQuery(job), ct);

    public ValueTask<JobPayload?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
        getJobResult.HandleAsync(new GetJobResultQuery(job), ct);

    public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
        getJobResult.HandleAsync<TResult>(new GetJobResultQuery(job), ct);

    public ValueTask<IReadOnlyList<JobCheckpointItem>> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
        getJobCheckpoints.HandleAsync(new GetJobCheckpointsQuery(job), ct);

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
    ) => raiseSignal.HandleAsync(job, name, value, actorKey, ct);

    public ValueTask<RaiseSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actorKey = null,
        CancellationToken ct = default
    ) => raiseSignal.HandleAsync(new RaiseSignalCommand(job, name, value, actorKey), ct);

    public JobInputTemplate? GetInputTemplate(string jobNamespace, string jobName) =>
        getInputTemplate.Handle(new GetInputTemplateQuery(jobNamespace, jobName));
}
