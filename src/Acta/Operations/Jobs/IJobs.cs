using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Acta;

/// <summary>
/// Jobs facade: enqueue (single and batch), reads (resolve, get, status, result), and the lifecycle and signal control
/// verbs. No out-of-handler alert verb: alerts come from <c>ctx.AlertAsync</c> and the automatic failure alerts. The
/// job control verbs take an optional expectedVersion where the catalog controls require one: each is a status-conditional
/// transition an operator fires by hand against a moving job, and a required token on a row that changes every few
/// seconds would turn "pause it" into a retry loop. Null writes unconditionally, serialized and audited; a caller
/// passing <see cref="GetJobResponse.Version"/> gets <see cref="ControlAction.VersionConflict"/>, not a lost update.
/// </summary>
public interface IJobs
{
    /// <summary>
    /// Enqueue one Job for asynchronous execution. Resolves <c>(JobNamespace, JobName)</c> from
    /// <paramref name="request"/> against the registered runtime and returns the internal
    /// <c>JobId</c>, the public <c>JobRef</c>, and the coarse <see cref="EnqueueJobAction"/>
    /// (<c>Inserted</c>, or <c>Deduplicated</c> when an existing row matched
    /// <see cref="EnqueueJobRequest.DeduplicationKey"/>).
    /// </summary>
    [OverloadResolutionPriority(1)]
    ValueTask<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default);

    /// <summary>
    /// Typed enqueue. Resolves <typeparamref name="TInput"/> to its registered job (namespace, name,
    /// payload format) from the generated manifest, serializes <paramref name="input"/>, and enqueues.
    /// Returns a <see cref="JobHandle"/> to follow the job; it converts implicitly to <see cref="JobLookup"/> for
    /// the read and control verbs. Throws when the input type is unregistered or ambiguous across namespaces (set
    /// <c>options.JobNamespace</c> or use <see cref="EnqueueAsync(EnqueueJobRequest, CancellationToken)"/>).
    /// </summary>
    ValueTask<JobHandle> EnqueueAsync<TInput>(TInput input, JobEnqueueOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Typed enqueue with fluent options: <c>jobs.EnqueueAsync(input, o =&gt; o.DeduplicationKey("final-key"))</c>.
    /// Identical semantics to passing the equivalent <see cref="JobEnqueueOptions"/>; the default
    /// implementation builds the options and forwards.
    /// </summary>
    ValueTask<JobHandle> EnqueueAsync<TInput>(TInput input, Action<JobEnqueueOptionsBuilder> configure, CancellationToken ct = default)
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new JobEnqueueOptionsBuilder();
        configure(builder);
        return EnqueueAsync(input, builder.Build(), ct);
    }

    /// <summary>
    /// Typed enqueue-and-wait without a result. Enqueues <paramref name="input"/>, then polls until the
    /// Job reaches a terminal state or the wait budget elapses. The returned <see cref="JobOutcome"/>
    /// reports success, failure, cancellation, or timeout and is returned, never thrown (call
    /// <see cref="JobOutcome.ThrowIfFailed"/> for the throwing path). A wait timeout stops the caller
    /// awaiting; the Job keeps running on its worker.
    /// </summary>
    ValueTask<JobOutcome> RunAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Typed enqueue-and-wait with a result. Like <see cref="RunAndWaitAsync{TInput}(TInput, JobExecutionOptions, CancellationToken)"/>, and on terminal
    /// <c>Succeeded</c> deserializes the handler's result to <typeparamref name="TResult"/>. The result is
    /// non-null by contract (Acta has no durable null payload); a handler returning null fails the Job.
    /// </summary>
    ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull;

    /// <summary>
    /// Enqueue a generated call: <c>jobs.EnqueueAsync(ShippingJobs.ShipOrder(order))</c>. The call names its job, so
    /// nothing is inferred from the input's type: the namespace and input format come from the manifest's registration.
    /// Throws when the manifest is unregistered, or bound to more than one namespace with no <c>JobNamespace</c> set.
    /// </summary>
    ValueTask<JobHandle> EnqueueAsync<TInput>(
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Enqueue a generated call of a job that returns a result. The <see cref="JobHandle{TResult}"/> waits for and reads
    /// the result as <typeparamref name="TResult"/>, which is checked against the job's registered result before the enqueue.
    /// </summary>
    ValueTask<JobHandle<TResult>> EnqueueAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull;

    /// <summary>
    /// Wait for the job identified by <paramref name="job"/> to reach a terminal state, up to <paramref name="timeout"/>
    /// (30 seconds when null). The outcome reports success, failure, cancellation, or timeout and is returned, never
    /// thrown; a timeout or a cancelled <paramref name="ct"/> stops the wait, never the job. Throws when no job matches,
    /// which includes a job enqueued in a transaction that has not committed.
    /// </summary>
    ValueTask<JobOutcome> WaitAsync(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// <see cref="WaitAsync(JobLookup, TimeSpan?, CancellationToken)"/>, and on success the result stored by the execution the
    /// wait saw succeed, read as <typeparamref name="TResult"/>; a success that stored none throws.
    /// </summary>
    ValueTask<JobOutcome<TResult>> WaitAsync<TResult>(JobLookup job, TimeSpan? timeout = null, CancellationToken ct = default)
        where TResult : notnull;

    /// <summary>
    /// A <see cref="JobHandle"/> on <paramref name="jobRef"/>, for following in one request or process a job another
    /// enqueued, or null when no job matches. Resolves the id once, so each of the handle's calls is one round trip; its
    /// <see cref="EnqueueJobResponse.Action"/> is <see cref="EnqueueJobAction.Deduplicated"/>, since the job already existed.
    /// </summary>
    async ValueTask<JobHandle?> GetHandleAsync(JobRef jobRef, CancellationToken ct = default) =>
        await GetJobIdAsync(jobRef, ct) is { } resolved ? new JobHandle(this, resolved.JobId, jobRef, EnqueueJobAction.Deduplicated) : null;

    /// <summary>
    /// Enqueue-and-wait for a generated call; same waiting semantics as the typed
    /// <see cref="RunAndWaitAsync{TInput}(TInput, JobExecutionOptions, CancellationToken)"/>, with the job named rather
    /// than inferred.
    /// </summary>
    ValueTask<JobOutcome> RunAndWaitAsync<TInput>(JobCall<TInput> job, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Enqueue-and-wait for a generated call of a job that returns a result, read as <typeparamref name="TResult"/> on success.
    /// </summary>
    ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull;

    /// <summary>
    /// Batched enqueue. One round-trip writes every <paramref name="requests"/> row and answers each in
    /// <see cref="EnqueueJobsResponse.Jobs"/>, positionally aligned, so <c>Jobs[i]</c> corresponds to <c>requests[i]</c>.
    /// Each carries the internal <c>JobId</c>, the public <c>JobRef</c>, and the coarse <see cref="EnqueueJobAction"/>.
    /// </summary>
    ValueTask<EnqueueJobsResponse> EnqueueBatchAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct = default);

    /// <summary>
    /// Transactional twin of <see cref="EnqueueAsync(EnqueueJobRequest, CancellationToken)"/>, sharing the caller's open
    /// <paramref name="transaction"/> and commit. For every transactional twin below: the caller owns the transaction
    /// (Acta never opens, commits, rolls back, disposes, or retries it); no worker wakeup is published; the
    /// <see cref="EnqueueJobResponse"/> is provisional until commit and dead after a rollback (SQLite hands a rolled-back
    /// id to the next insert); any exception means rolling back the whole business transaction. No handle and no
    /// transactional <c>RunAndWaitAsync</c>: the job is invisible until commit.
    /// </summary>
    [OverloadResolutionPriority(1)]
    ValueTask<EnqueueJobResponse> EnqueueAsync(DbTransaction transaction, EnqueueJobRequest request, CancellationToken ct = default);

    /// <summary>
    /// Transactional twin of <see cref="EnqueueAsync{TInput}(TInput, JobEnqueueOptions, CancellationToken)"/>.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Transactional twin of the fluent typed enqueue; the default implementation builds the options and
    /// forwards to the transactional typed overload.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
        where TInput : notnull
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new JobEnqueueOptionsBuilder();
        configure(builder);
        return EnqueueAsync(transaction, input, builder.Build(), ct);
    }

    /// <summary>
    /// Transactional twin of the call enqueue
    /// (<see cref="EnqueueAsync{TInput}(JobCall{TInput}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>).
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Transactional twin of the result-bearing call enqueue
    /// (<see cref="EnqueueAsync{TInput, TResult}(JobCall{TInput, TResult}, Action{JobEnqueueOptionsBuilder}, CancellationToken)"/>);
    /// the result type is checked as there.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull;

    /// <summary>
    /// Transactional twin of <see cref="EnqueueBatchAsync(IReadOnlyList{EnqueueJobRequest}, CancellationToken)"/>:
    /// the whole batch inserts through <paramref name="transaction"/> atomically and publishes no wakeup.
    /// </summary>
    ValueTask<EnqueueJobsResponse> EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resolve <paramref name="job"/> to the internal <c>JobId</c> in <see cref="GetJobIdResponse"/>, or <c>null</c> when no row
    /// matches. A <see cref="JobLookupKind.JobId"/> lookup returns the id directly with no DB
    /// round-trip; a <see cref="JobLookupKind.JobRef"/> lookup resolves over the unique
    /// <c>job_ref</c> index; a <see cref="JobLookupKind.DeduplicationKey"/> lookup matches root jobs only
    /// (<c>parent_job_id IS NULL</c>). Pin the returned id and reuse it across polls and mutations.
    /// </summary>
    ValueTask<GetJobIdResponse?> GetJobIdAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Read the full <see cref="GetJobResponse"/> for the job identified by <paramref name="job"/>.
    /// Returns <c>null</c> when no row matches. Resolves <paramref name="job"/> to a
    /// <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<GetJobResponse?> GetAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Explain the durable state of the job identified by <paramref name="job"/> in plain English:
    /// its status and what it means, the signal or timer it waits on, which steps ran, its execution
    /// lease and the owning worker's liveness, what crash recovery will do, and the operator's
    /// next move. Returns <c>null</c> when no row matches. Composed from the same durable rows an
    /// operator could read directly - SQL is the operational interface. Resolves <paramref name="job"/> to a
    /// <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<GetJobExplanationResponse?> ExplainAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Read a compact lineage map for the job identified by <paramref name="job"/>: its ancestor
    /// context up to the lineage root, the job itself with its steps and the durable wait it is blocked
    /// on, and its direct children (capped at <see cref="GetJobLineageMapOptions.ChildLimit"/>, with
    /// <see cref="GetJobLineageMapResponse.ChildrenHasMore"/> flagging a truncated set). Returns <c>null</c> when no
    /// row matches. V1 is shallow: ancestors are context only and children are not expanded recursively.
    /// Resolves <paramref name="job"/> to a <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<GetJobLineageMapResponse?> GetLineageMapAsync(
        JobLookup job,
        GetJobLineageMapOptions? options = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Cheap status probe. Returns the current <see cref="JobStatusCode"/> in <see cref="GetJobStatusResponse"/> for the job identified
    /// by <paramref name="job"/>, or <c>null</c> when no row matches. Resolves
    /// <paramref name="job"/> to a <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<GetJobStatusResponse?> GetStatusAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Fetch the stored input payload the job identified by <paramref name="job"/> was enqueued with,
    /// or <c>null</c> when no row matches or the job carries no input (a no-input job). Non-blocking
    /// point-in-time read: it does not wait for completion and reflects any <see cref="UpdateJobInputAsync"/>
    /// amendment already applied.
    /// </summary>
    ValueTask<GetJobInputResponse?> GetInputAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Fetch the result payload the job identified by <paramref name="job"/> stored, or <c>null</c> when it
    /// holds none (still running, failed, restarted, or a handler with no return value). A job keeps one result: a
    /// recurring job's successful occurrence replaces the last one, and every claim and restart clears it.
    /// Non-blocking point-in-time read: it does not wait for completion. To await the answer, use
    /// <see cref="WaitAsync{TResult}"/>.
    /// </summary>
    ValueTask<GetResultResponse?> GetResultAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Read the durable checkpoint slots (variables, signals, sleep timers, the progress slot, child
    /// latches) recorded for the job identified by <paramref name="job"/>. Returns empty <c>Items</c> when the
    /// job has no slots, and <c>null</c> when no job matches. Non-blocking point-in-time read.
    /// </summary>
    ValueTask<GetCheckpointsResponse?> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Typed convenience over <see cref="GetResultAsync(JobLookup, CancellationToken)"/>: deserializes the
    /// stored result payload to <typeparamref name="TResult"/> via the registered serializer, or returns
    /// <c>default</c> when the job has produced no result. Non-blocking (does not wait for completion).
    /// </summary>
    ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default);

    /// <summary>
    /// Cancel the job identified by <paramref name="job"/>: any non-terminal status moves to <c>Cancelled</c>, and the
    /// cancel cascades to its non-terminal descendants (event reason <c>parent-cancelled</c>), running to the end when
    /// <paramref name="ct"/> stops the wait for it. <paramref name="reasonMessage"/> is persisted on the row and as the
    /// audit event's message, and <paramref name="actor"/> is recorded in the audit event's details as the operator
    /// identity (the authenticated principal name; null when unknown). An already-terminal job is
    /// <see cref="ControlAction.Rejected"/>; a missing job is <see cref="ControlAction.NotFound"/>.
    /// </summary>
    ValueTask<CancelJobResponse> CancelAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Pause the job identified by <paramref name="job"/>: a job at or before <c>Ready</c> moves to <c>Paused</c>.
    /// Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>; <paramref name="reasonMessage"/> is persisted on
    /// the row and the audit event, and <paramref name="actor"/> is recorded on the audit event as the operator
    /// identity (the authenticated principal name; null when unknown). A running or terminal job is
    /// <see cref="ControlAction.Rejected"/>. <see cref="RescheduleAsync"/> re-arms a <c>Paused</c> job to
    /// <c>Ready</c>, the documented path out of <c>Paused</c>.
    /// </summary>
    ValueTask<PauseJobResponse> PauseAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resume the job identified by <paramref name="job"/>: a <c>Paused</c> job moves to <c>Ready</c>
    /// to run now. Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>;
    /// <paramref name="reasonMessage"/> is recorded on the audit event only (the row's reason is
    /// cleared). <paramref name="actor"/> is recorded on the audit event as the operator identity (e.g. the
    /// authenticated principal name); null when unknown. A non-paused job is <see cref="ControlAction.Rejected"/>.
    /// </summary>
    ValueTask<ResumeJobResponse> ResumeAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Restart the job identified by <paramref name="job"/>: any status except <c>Executing</c> moves to <c>Ready</c>
    /// to run now, with the failure budget reset, the retention deadline cleared, and the attempt counter unchanged.
    /// Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>; <paramref name="reasonMessage"/> and
    /// <paramref name="actor"/> (the operator identity; null when unknown) go on the audit event only. An executing
    /// job is <see cref="ControlAction.Rejected"/>. A laned job restarts in place under its lane's lock, <c>Blocked</c>
    /// while another member runs or an older one is unfinished, then runs first, since promotion takes the lowest id.
    /// </summary>
    ValueTask<RestartJobResponse> RestartAsync(
        JobLookup job,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Move the job identified by <paramref name="job"/>'s next-run instant to <paramref name="nextRunAtUtc"/>, or to
    /// now when null: the job page's "run now". Applies to Paused/Suspended/Ready rows (a paused or suspended row is
    /// re-armed Ready so the new instant actually fires); an in-flight or terminal row is rejected. An <c>Idle</c>
    /// permanent slot, or a Paused one with no firing outstanding, takes a manual firing at the instant, as a
    /// schedule trigger admits one, and returns to <c>Idle</c> when it settles. Audited as job.rescheduled with
    /// <paramref name="reasonMessage"/> and <paramref name="actor"/> (the operator identity; null when unknown).
    /// </summary>
    ValueTask<RescheduleJobResponse> RescheduleAsync(
        JobLookup job,
        DateTime? nextRunAtUtc = null,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Change the claim priority of the job identified by <paramref name="job"/>: any non-terminal
    /// row (including in-flight) accepts <paramref name="priority"/>, which affects only its next
    /// claim; status and cursor are unchanged. A terminal job is <see cref="ControlAction.Rejected"/>.
    /// The transition is audited (job.reprioritized); <paramref name="reasonMessage"/> is persisted on
    /// the audit event and <paramref name="actor"/> is recorded on the audit event as the operator
    /// identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<ReprioritizeJobResponse> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actor = null,
        int? expectedVersion = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Amend the stored input payload of the job identified by <paramref name="job"/>, in any status except
    /// <c>Executing</c>, where a handler may already have read it: an in-flight job is
    /// <see cref="ControlAction.Rejected"/>, a missing one <see cref="ControlAction.NotFound"/>. On success the input
    /// and format are replaced and the transition is audited (<c>job.input-amended</c>); the event detail carries the
    /// previous payload's format name and byte count, never the payload, so the amended-away value cannot outlive the
    /// job's payload retention. <paramref name="reasonMessage"/> and <paramref name="actor"/> go on the audit event.
    /// </summary>
    ValueTask<UpdateJobInputResponse> UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Hard-delete the terminal job identified by <paramref name="job"/>: its <c>events</c> and <c>alerts</c> rows,
    /// then the job row, which cascades to its runtime, schedule, step, result, checkpoint, and tag rows. A
    /// non-terminal job is <see cref="ControlAction.Rejected"/>, and so is a terminal job with children (deleting it
    /// would orphan their lineage; <c>parent_job_id</c> has no cascade) or a child whose parent is not terminal (the
    /// parent's replay dedupes onto it and reads its result). Always emits <c>job.purged</c>, with a null job and the
    /// purged ref and name on its <c>ReasonMessage</c>, stamped <paramref name="actor"/>; there is no caller reason.
    /// </summary>
    ValueTask<PurgeJobResponse> PurgeAsync(JobLookup job, string? actor = null, CancellationToken ct = default);

    /// <summary>
    /// Typed-payload variant of
    /// <see cref="RaiseSignalAsync(JobLookup, string, JobPayload, string?, CancellationToken)"/>. <paramref name="value"/> is
    /// JSON-serialized and stored on the slot; a handler reads it via
    /// <c>ctx.WaitSignalAsync&lt;T&gt;(name)</c>. <paramref name="actor"/> is recorded on the audit event
    /// as the operator identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<RaiseSignalResponse> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Pre-formed-payload variant of
    /// <see cref="RaiseSignalAsync(JobLookup, string, JobPayload, string?, CancellationToken)"/>: stores
    /// <paramref name="value"/> on the slot verbatim under its <c>Format</c>, read by
    /// <c>ctx.WaitSignalAsync&lt;T&gt;(name)</c>; <see cref="JobPayload.None"/> raises a presence-only signal. The HTTP
    /// signal endpoint uses it to pass a request body through as JSON without binding the handler's type.
    /// <paramref name="actor"/> is recorded on the audit event as the operator identity; null when unknown.
    /// </summary>
    ValueTask<RaiseSignalResponse> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// The input contract of a registered job, read from the in-process generated manifest: no
    /// database round-trip, no reflection. Returns null when this host has not registered the job's
    /// manifest (an enqueue-only dashboard pointed at a shared ledger), so callers degrade quietly.
    /// </summary>
    GetInputTemplateResponse? GetInputTemplate(string jobNamespace, string jobName);
}
