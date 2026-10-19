using System.Data.Common;

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
    ValueTask<EnqueueJobResponse> EnqueueAsync(EnqueueJobRequest request, CancellationToken ct = default);

    /// <summary>
    /// Typed enqueue. Resolves <typeparamref name="TInput"/> to its registered job (namespace, name,
    /// payload format) from the generated manifest, serializes <paramref name="input"/>, and enqueues.
    /// Returns the same <see cref="EnqueueJobResponse"/> as the raw path; it converts implicitly to
    /// <see cref="JobLookup"/> for the read and control verbs. Throws when the input type is
    /// unregistered or ambiguous across namespaces (set <c>options.Namespace</c> or use
    /// <see cref="EnqueueAsync(EnqueueJobRequest, CancellationToken)"/>).
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(TInput input, JobEnqueueOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Typed enqueue with fluent options: <c>jobs.EnqueueAsync(input, o =&gt; o.DeduplicationKey("final-key"))</c>.
    /// Identical semantics to passing the equivalent <see cref="JobEnqueueOptions"/>; the default
    /// implementation builds the options and forwards.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        TInput input,
        Action<JobEnqueueOptionsBuilder> configure,
        CancellationToken ct = default
    )
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
    /// Typed enqueue-and-wait with a result. Like <see cref="RunAndWaitAsync{TInput}"/>, and on terminal
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
    /// Enqueue against an explicit generated <see cref="JobContract{TInput}"/>. Resolves the
    /// namespace and input format from the contract's manifest binding, serializes
    /// <paramref name="input"/>, and enqueues. No input-type inference. Throws when the manifest is
    /// unregistered, or bound to more than one namespace with <c>options.Namespace</c> unset.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Fire-and-forget enqueue of a result-bearing job by contract; the result is dropped (read it
    /// from the JobRef once terminal, or call the contract RunAndWaitAsync to wait). Explicit overload so
    /// the path is discoverable rather than relying on the implicit two-arg to one-arg conversion.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Enqueue a no-input job by contract. Valid when the contract's job declares no input.
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync(JobContract<NoInput> job, JobEnqueueOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Enqueue-and-wait against an explicit result-bearing contract. Same waiting semantics as the
    /// typed <see cref="RunAndWaitAsync{TInput, TResult}(TInput, JobExecutionOptions, CancellationToken)"/>,
    /// with the target named rather than inferred.
    /// </summary>
    ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
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
    /// Transactional twin of <see cref="EnqueueAsync(EnqueueJobRequest, CancellationToken)"/>: inserts the job through
    /// the caller's open <paramref name="transaction"/>, so a same-database business mutation and the enqueue share one
    /// commit. The rules hold for every transactional twin below: the caller owns the transaction (Acta never opens,
    /// commits, rolls back, disposes, or retries it); no worker wakeup is published, since Acta cannot know when the
    /// caller commits; the <see cref="EnqueueJobResponse"/> is provisional until commit, and any exception means rolling
    /// back the whole business transaction. No transactional <c>RunAndWaitAsync</c>: the job is invisible until commit.
    /// </summary>
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
    /// Transactional twin of the input-contract enqueue
    /// (<see cref="EnqueueAsync{TInput}(JobContract{TInput}, TInput, JobEnqueueOptions, CancellationToken)"/>).
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Transactional twin of the result-bearing contract enqueue used fire-and-forget
    /// (<see cref="EnqueueAsync{TInput, TResult}(JobContract{TInput, TResult}, TInput, JobEnqueueOptions, CancellationToken)"/>).
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Transactional twin of the no-input contract enqueue
    /// (<see cref="EnqueueAsync(JobContract{NoInput}, JobEnqueueOptions, CancellationToken)"/>).
    /// </summary>
    ValueTask<EnqueueJobResponse> EnqueueAsync(
        DbTransaction transaction,
        JobContract<NoInput> job,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    );

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
    /// (<c>parent_id IS NULL</c>). Pin the returned id and reuse it across polls and mutations.
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
    /// Non-blocking point-in-time read: it does not wait for completion. To await the answer, poll
    /// <see cref="GetStatusAsync"/> until terminal and then read here.
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
    /// cancel cascades to the job's non-terminal descendant subtree (<c>reason = ParentCancelled</c>). Stamps
    /// <c>actor = Operator</c>, <c>reason = ControlManual</c> on the target; <paramref name="reasonMessage"/> is
    /// persisted on the row and the audit event, and <paramref name="actor"/> is recorded on the audit event as the
    /// operator identity (the authenticated principal name; null when unknown). An already-terminal job is
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
    /// Move the job identified by <paramref name="job"/>'s next-run instant to
    /// <paramref name="nextRunAtUtc"/>. Applies to Paused/Suspended/Ready rows (a paused or suspended
    /// row is re-armed Ready so the new instant actually fires); an in-flight or terminal row is
    /// rejected. The transition is audited (job.rescheduled); <paramref name="reasonMessage"/> is
    /// persisted on the audit event and <paramref name="actor"/> is recorded on the audit event as
    /// the operator identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<RescheduleJobResponse> RescheduleAsync(
        JobLookup job,
        DateTime nextRunAtUtc,
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
    /// would orphan their lineage; <c>parent_id</c> has no cascade) or a child whose parent is not terminal (the
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
