using System.Data.Common;
using System.Diagnostics;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Signals;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// <see cref="IJobs"/> implementation. Routing resolves namespace and definition by SQL JOIN in the
/// provider's <c>InsertJob</c> against the catalog tables; no in-memory map is required here, so
/// the same code path serves worker-hosted and enqueue-only deployments.
/// </summary>
internal sealed class JobsApi(
    IJobPayloadSerializerRegistry serializers,
    JobTypeIndex typeIndex,
    JobContractIndex contractIndex,
    JobDescriptorIndex descriptorIndex,
    IWorkerWakeup wakeup,
    JobsService jobsService,
    SignalService signalService
) : IJobs
{
    public ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct = default) =>
        jobsService.EnqueueAsync(request, ct);

    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct = default
    ) => jobsService.EnqueueBatchAsync(requests, ct);

    // Transactional twins: build the same wire request as the owned overloads, then insert it through
    // the caller's transaction with no wakeup (see IJobs). The fluent typed twin is a default interface
    // method that forwards here after building its options.
    public ValueTask<JobEnqueueOutcome> EnqueueAsync(
        DbTransaction transaction,
        JobEnqueueRequest request,
        CancellationToken ct = default
    ) => jobsService.EnqueueInTransactionAsync(transaction, request, ct);

    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct = default
    ) => jobsService.EnqueueBatchInTransactionAsync(transaction, requests, ct);

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return EnqueueAsync(transaction, BuildTypedRequest(input, options), ct);
    }

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return EnqueueAsync(transaction, BuildContractRequest(job, input, options), ct);
    }

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
    ) => EnqueueAsync(transaction, BuildContractRequest(job, default, options), ct);

    public async ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        options = ValidatedExecutionOptions(options);

        var outcome = await EnqueueAsync(input, options, ct);
        var (snapshot, timedOut) = await AwaitTerminalAsync(outcome.JobId, options, ct);

        return timedOut
            ? JobOutcome.TimedOut(outcome.JobId, snapshot?.Status ?? JobStatusCode.Ready)
            : snapshot!.Status switch
            {
                JobStatusCode.Succeeded => JobOutcome.Succeeded(outcome.JobId),
                JobStatusCode.Cancelled => JobOutcome.Cancelled(outcome.JobId),
                _ => JobOutcome.Failed(outcome.JobId),
            };
    }

    public async ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        options = ValidatedExecutionOptions(options);

        var outcome = await EnqueueAsync(input, options, ct);
        return await AwaitTypedResultAsync<TResult>(outcome.JobId, options, ct);
    }

    // Validated before EnqueueAsync, so invalid wait options never enqueue a job.
    private static JobExecutionOptions ValidatedExecutionOptions(JobExecutionOptions? options)
    {
        options ??= new JobExecutionOptions();

        if (options.WaitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WaitTimeout, "WaitTimeout must be greater than zero.");
        }

        return options.PollInterval <= TimeSpan.Zero
            ? throw new ArgumentOutOfRangeException(nameof(options), options.PollInterval, "PollInterval must be greater than zero.")
            : options;
    }

    // Resolve the input type to its route, serialize, and fold the typed options onto a wire request.
    // A None-format input (no-input job) carries JobPayload.None directly, with no serializer round-trip.
    private JobEnqueueRequest BuildTypedRequest<TInput>(TInput input, JobEnqueueOptions? options)
        where TInput : notnull
    {
        var route = typeIndex.Resolve(typeof(TInput), options?.JobNamespace);
        var payload = route.InputFormat.IsNone ? JobPayload.None : serializers.Resolve(route.InputFormat.Id).Serialize(input);

        return new JobEnqueueRequest(
            JobNamespace: route.Namespace,
            JobName: route.JobName,
            Input: payload,
            DeduplicationKey: options?.DeduplicationKey,
            CorrelationKey: options?.CorrelationKey,
            ExclusiveKey: options?.ExclusiveKey,
            Priority: options?.Priority,
            Tags: options?.Tags,
            NextRunAtUtc: options?.NextRunAtUtc,
            DelaySeconds: options?.DelaySeconds,
            ParentJobId: options?.ParentJobId,
            TenantKey: options?.TenantKey,
            OverrideParentTenant: options?.OverrideParentTenant ?? false
        );
    }

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return EnqueueAsync(BuildTypedRequest(input, options), ct);
    }

    public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        return EnqueueAsync(BuildContractRequest(job, input, options), ct);
    }

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
    ) => EnqueueAsync(BuildContractRequest(job, default, options), ct);

    public async ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        options = ValidatedExecutionOptions(options);
        var outcome = await EnqueueAsync(BuildContractRequest((JobContract<TInput>)job, input, options, typeof(TResult)), ct);
        return await AwaitTypedResultAsync<TResult>(outcome.JobId, options, ct);
    }

    // Resolve the contract's route from the manifest binding, validate the contract's compile-time
    // types against the registered descriptor (guards hand-built contracts), serialize, and fold the
    // options onto a wire request. A None-format input carries JobPayload.None with no serializer
    // round-trip (the no-input overload passes a default input that is never read).
    private JobEnqueueRequest BuildContractRequest<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options,
        Type? expectedResultType = null
    )
    {
        var manifestType = ValidateContract(job);
        var route = contractIndex.Resolve(manifestType, job.JobName, options?.JobNamespace);

        if (route.InputType != typeof(TInput))
        {
            throw new ArgumentException(
                $"JobContract for '{route.JobName}' expects input '{route.InputType.FullName}' but was used with "
                    + $"'{typeof(TInput).FullName}'. Use the generated manifest member.",
                nameof(job)
            );
        }
        if (expectedResultType is not null && route.OutputType != expectedResultType)
        {
            throw new ArgumentException(
                $"JobContract for '{route.JobName}' produces result '{route.OutputType?.FullName ?? "none"}' but was used "
                    + $"with TResult '{expectedResultType.FullName}'.",
                nameof(job)
            );
        }

        var payload = route.InputFormat.IsNone ? JobPayload.None : serializers.Resolve(route.InputFormat.Id).Serialize(input);

        return new JobEnqueueRequest(
            JobNamespace: route.Namespace,
            JobName: route.JobName,
            Input: payload,
            DeduplicationKey: options?.DeduplicationKey,
            CorrelationKey: options?.CorrelationKey,
            ExclusiveKey: options?.ExclusiveKey,
            Priority: options?.Priority,
            NextRunAtUtc: options?.NextRunAtUtc,
            DelaySeconds: options?.DelaySeconds,
            Tags: options?.Tags,
            ParentJobId: options?.ParentJobId,
            TenantKey: options?.TenantKey,
            OverrideParentTenant: options?.OverrideParentTenant ?? false
        );
    }

    // A default(JobContract<T>) or otherwise malformed contract is rejected with an intentional
    // ArgumentException rather than an incidental NullReferenceException downstream.
    private static Type ValidateContract<TInput>(JobContract<TInput> job)
    {
        if (job.ManifestType is null || string.IsNullOrWhiteSpace(job.JobName))
        {
            throw new ArgumentException("JobContract is uninitialized (default); use a generated manifest member.", nameof(job));
        }
        return !typeof(IJobManifest).IsAssignableFrom(job.ManifestType)
            ? throw new ArgumentException($"JobContract.ManifestType '{job.ManifestType.FullName}' is not an IJobManifest.", nameof(job))
            : job.ManifestType;
    }

    // Wait for terminal status then materialize the typed result. Shared by the type-inference and
    // contract RunAndWaitAsync overloads. A Succeeded job that stored no result is a caller contract
    // mismatch (throws), never a default(TResult).
    private async ValueTask<JobOutcome<TResult>> AwaitTypedResultAsync<TResult>(
        long jobId,
        JobExecutionOptions options,
        CancellationToken ct
    )
        where TResult : notnull
    {
        var job = JobLookup.ById(jobId);
        var (snapshot, timedOut) = await AwaitTerminalAsync(jobId, options, ct);

        if (timedOut)
        {
            return JobOutcome<TResult>.TimedOut(jobId, snapshot?.Status ?? JobStatusCode.Ready);
        }

        switch (snapshot!.Status)
        {
            case JobStatusCode.Succeeded:
                // No stored result on a succeeded job means one of two things, and this throws for
                // both rather than handing back a default TResult: the job genuinely returns nothing,
                // or its result was dropped for exceeding MaxInlinePayloadBytes (the events timeline
                // carries job.result-oversized when that is what happened).
                var payload =
                    await GetResultAsync(job, ct)
                    ?? throw new InvalidOperationException(
                        $"Job {jobId} ('{snapshot.JobName}') succeeded but stored no result. Either it is "
                            + "result-less, in which case use the non-result RunAndWaitAsync overload, or its "
                            + "result exceeded MaxInlinePayloadBytes and was dropped; the job's events say which."
                    );
                var value = serializers.Resolve(payload.Format.Id).Deserialize<TResult>(payload);
                return JobOutcome<TResult>.Succeeded(jobId, value!);
            case JobStatusCode.Cancelled:
                return JobOutcome<TResult>.Cancelled(jobId);
            default:
                return JobOutcome<TResult>.Failed(jobId);
        }
    }

    // Client-side wait loop: poll the terminal status until reached or the local wait budget expires.
    // The budget is measured with Stopwatch (local monotonic time), not IActaClock; a wait timeout is a
    // caller-side concern, not a durable server decision; the Job keeps running after the caller stops.
    // Between polls the loop waits on the job's completion channel, so a completion wake from a
    // colocated worker (or a cross-process transport) is observed immediately; without one the
    // PollInterval is the discovery floor, so split deployments without a shared transport keep pure
    // polling latency.
    private async ValueTask<(JobDetail? Snapshot, bool TimedOut)> AwaitTerminalAsync(
        long jobId,
        JobExecutionOptions options,
        CancellationToken ct
    )
    {
        var job = JobLookup.ById(jobId);
        var channel = WorkerWakeupChannel.JobCompletion(jobId);
        var start = Stopwatch.GetTimestamp();
        JobDetail? last = null;

        while (true)
        {
            if (await GetAsync(job, ct) is { } snapshot)
            {
                last = snapshot;
                if (snapshot.Status.IsTerminal)
                {
                    return (snapshot, false);
                }
            }

            var remaining = options.WaitTimeout - Stopwatch.GetElapsedTime(start);
            if (remaining <= TimeSpan.Zero)
            {
                return (last, true);
            }

            await wakeup.WaitAsync(channel, remaining < options.PollInterval ? remaining : options.PollInterval, ct);
        }
    }

    public ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct = default) => jobsService.GetJobIdAsync(job, ct);

    public ValueTask<JobDetail?> GetAsync(JobLookup job, CancellationToken ct = default) => jobsService.GetAsync(job, ct);

    public ValueTask<JobExplanation?> ExplainAsync(JobLookup job, CancellationToken ct = default) => jobsService.ExplainAsync(job, ct);

    public ValueTask<JobLineageMap?> GetLineageMapAsync(
        JobLookup job,
        JobLineageMapOptions? options = null,
        CancellationToken ct = default
    ) => jobsService.GetLineageMapAsync(job, options, ct);

    public ValueTask<JobStatusCode?> GetStatusAsync(JobLookup job, CancellationToken ct = default) => jobsService.GetStatusAsync(job, ct);

    public ValueTask<JobPayload?> GetInputAsync(JobLookup job, CancellationToken ct = default) => jobsService.GetInputAsync(job, ct);

    public ValueTask<JobPayload?> GetResultAsync(JobLookup job, CancellationToken ct = default) => jobsService.GetResultAsync(job, ct);

    public ValueTask<IReadOnlyList<JobCheckpointItem>> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
        jobsService.GetCheckpointsAsync(job, ct);

    public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
        jobsService.GetResultAsync<TResult>(job, ct);

    public ValueTask<JobControlResult> CancelAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.CancelAsync(job, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> PauseAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.PauseAsync(job, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> ResumeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.ResumeAsync(job, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> RestartAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.RestartAsync(job, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> RescheduleAsync(
        JobLookup job,
        DateTime nextRunAtUtc,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.RescheduleAsync(job, nextRunAtUtc, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.ReprioritizeAsync(job, priority, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => jobsService.UpdateJobInputAsync(job, input, reasonMessage, actorKey, ct);

    public ValueTask<JobControlResult> PurgeAsync(JobLookup job, string? actorKey = null, CancellationToken ct = default) =>
        jobsService.PurgeAsync(job, actorKey, ct);

    public ValueTask<JobControlResult> RaiseSignalAsync(
        JobLookup job,
        string name,
        string? actorKey = null,
        CancellationToken ct = default
    ) => RaiseSignalCoreAsync(job, name, valueFormatId: 0, value: null, actorKey, ct);

    public ValueTask<JobControlResult> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actorKey = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(value);
        // Through the registry (UseJsonPayloads-aware) rather than the reflection-based static helper.
        var payload = serializers.Resolve(JobPayloadFormat.Json.Id).Serialize(value);
        return RaiseSignalCoreAsync(job, name, payload.Format.Id, payload.Data.ToArray(), actorKey, ct);
    }

    public ValueTask<JobControlResult> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        value.IsNone
            ? RaiseSignalCoreAsync(job, name, valueFormatId: 0, value: null, actorKey, ct)
            : RaiseSignalCoreAsync(job, name, value.Format.Id, value.Data.ToArray(), actorKey, ct);

    // Raise sets the (job_id, name) slot last-writer-wins and conditionally releases a Suspended job.
    // The actor (Operator) and reason (SignalReleased) are stamped here, never accepted from the caller.
    private ValueTask<JobControlResult> RaiseSignalCoreAsync(
        JobLookup job,
        string name,
        byte valueFormatId,
        byte[]? value,
        string? actorKey,
        CancellationToken ct
    ) => signalService.RaiseAsync(job, name, valueFormatId, value, actorKey, ct);

    public JobInputTemplate? GetInputTemplate(string jobNamespace, string jobName) =>
        descriptorIndex.Find(jobNamespace, jobName) is { } descriptor
            ? new JobInputTemplate(
                descriptor.InputType.FullName ?? descriptor.InputType.Name,
                descriptor.InputPayloadFormat,
                descriptor.InputTemplateJson
            )
            : null;
}
