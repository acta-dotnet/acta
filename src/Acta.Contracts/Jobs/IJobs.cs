namespace Acta;

/// <summary>
/// Jobs facade: enqueue (single and batch), a read surface (resolve, get, status, result), and the
/// lifecycle and signal control verbs. Same-process and cross-process callers both reach the durable
/// substrate through this seam.
/// </summary>
public interface IJobs
{
    /// <summary>
    /// Enqueue one Job for asynchronous execution. Resolves <c>(JobNamespace, JobName)</c> from
    /// <paramref name="request"/> against the registered runtime and returns the internal
    /// <c>JobId</c>, the public <c>JobRef</c>, and the coarse <see cref="JobEnqueueAction"/>
    /// (<c>Inserted</c>, or <c>Deduplicated</c> when an existing row matched
    /// <see cref="JobEnqueueRequest.DeduplicationKey"/>).
    /// </summary>
    ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct = default);

    /// <summary>
    /// Typed enqueue. Resolves <typeparamref name="TInput"/> to its registered job (namespace, name,
    /// payload format) from the generated manifest, serializes <paramref name="input"/>, and enqueues.
    /// Returns the same <see cref="JobEnqueueOutcome"/> as the raw path; it converts implicitly to
    /// <see cref="JobLookup"/> for the read and control verbs. Throws when the input type is
    /// unregistered or ambiguous across namespaces (set <c>options.Namespace</c> or use
    /// <see cref="EnqueueAsync(JobEnqueueRequest, CancellationToken)"/>).
    /// </summary>
    ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(TInput input, JobEnqueueOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Typed enqueue with fluent options: <c>jobs.EnqueueAsync(input, o =&gt; o.DeduplicationKey("final-key"))</c>.
    /// Identical semantics to passing the equivalent <see cref="JobEnqueueOptions"/>; the default
    /// implementation builds the options and forwards.
    /// </summary>
    ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
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
    ValueTask<JobOutcome> ExecuteAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options = null, CancellationToken ct = default)
        where TInput : notnull;

    /// <summary>
    /// Typed enqueue-and-wait with a result. Like <see cref="ExecuteAndWaitAsync{TInput}"/>, and on terminal
    /// <c>Done</c> deserializes the handler's result to <typeparamref name="TResult"/>. The result is
    /// non-null by contract (Acta has no durable null payload); a handler returning null fails the Job.
    /// </summary>
    ValueTask<JobOutcome<TResult>> ExecuteAndWaitAsync<TInput, TResult>(
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
    ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Fire-and-forget enqueue of a result-bearing job by contract; the result is dropped (read it
    /// from the JobRef once terminal, or call the contract ExecuteAndWaitAsync to wait). Explicit overload so
    /// the path is discoverable rather than relying on the implicit two-arg to one-arg conversion.
    /// </summary>
    ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull;

    /// <summary>
    /// Enqueue a no-input job by contract. Valid when the contract's job declares no input.
    /// </summary>
    ValueTask<JobEnqueueOutcome> EnqueueAsync(JobContract<NoInput> job, JobEnqueueOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Enqueue-and-wait against an explicit result-bearing contract. Same waiting semantics as the
    /// typed <see cref="ExecuteAndWaitAsync{TInput, TResult}(TInput, JobExecutionOptions, CancellationToken)"/>,
    /// with the target named rather than inferred.
    /// </summary>
    ValueTask<JobOutcome<TResult>> ExecuteAndWaitAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobExecutionOptions? options = null,
        CancellationToken ct = default
    )
        where TInput : notnull
        where TResult : notnull;

    /// <summary>
    /// Batched enqueue. One round-trip writes every <paramref name="requests"/> row; the returned list
    /// is positionally aligned, so <c>outcomes[i]</c> corresponds to <c>requests[i]</c>. Each outcome
    /// carries the internal <c>JobId</c>, the public <c>JobRef</c>, and the coarse
    /// <see cref="JobEnqueueAction"/>.
    /// </summary>
    ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resolve <paramref name="lookup"/> to the internal <c>JobId</c>, or <c>null</c> when no row
    /// matches. A <see cref="JobLookupKind.JobId"/> lookup returns the id directly with no DB
    /// round-trip; a <see cref="JobLookupKind.JobRef"/> lookup resolves over the unique
    /// <c>job_ref</c> index; a <see cref="JobLookupKind.DeduplicationKey"/> lookup matches root jobs only
    /// (<c>parent_id IS NULL</c>). Pin the returned id and reuse it across polls and mutations.
    /// </summary>
    ValueTask<long?> ResolveJobIdAsync(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Read the full <see cref="JobSnapshot"/> for the job identified by <paramref name="lookup"/>.
    /// Returns <c>null</c> when no row matches. Resolves <paramref name="lookup"/> to a
    /// <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<JobSnapshot?> GetAsync(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Explain the durable state of the job identified by <paramref name="lookup"/> in plain English:
    /// its status and what it means, the signal or timer it waits on, which steps ran, its execution
    /// lease and the owning worker's liveness, what <c>sys.recovery</c> will do, and the operator's
    /// next move. Returns <c>null</c> when no row matches. Composed from the same durable rows an
    /// operator could read directly - SQL is the operational interface. Resolves <paramref name="lookup"/> to a
    /// <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<JobExplanation?> ExplainAsync(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Read a compact lineage map for the job identified by <paramref name="lookup"/>: its ancestor
    /// context up to the lineage root, the job itself with its steps and the durable wait it is blocked
    /// on, and its direct children (capped at <see cref="JobLineageMapOptions.ChildLimit"/>, with
    /// <see cref="JobLineageMap.ChildrenHasMore"/> flagging a truncated set). Returns <c>null</c> when no
    /// row matches. V1 is shallow: ancestors are context only and children are not expanded recursively.
    /// Resolves <paramref name="lookup"/> to a <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<JobLineageMap?> GetLineageMapAsync(JobLookup lookup, JobLineageMapOptions? options = null, CancellationToken ct = default);

    /// <summary>
    /// Cheap status probe. Returns the current <see cref="JobStatusCode"/> for the job identified
    /// by <paramref name="lookup"/>, or <c>null</c> when no row matches. Resolves
    /// <paramref name="lookup"/> to a <c>JobId</c> first; pin the resolved id when polling.
    /// </summary>
    ValueTask<JobStatusCode?> GetStatusAsync(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Fetch the latest result payload the job identified by <paramref name="lookup"/> has produced, or
    /// <c>null</c> when it has produced none (still running, failed, or a handler with no return value).
    /// Non-blocking point-in-time read: it does not wait for completion. To await the answer, poll
    /// <see cref="GetStatusAsync"/> until terminal and then read here.
    /// </summary>
    ValueTask<JobPayload?> GetResultAsync(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Typed variant of <see cref="GetResultAsync(JobLookup, CancellationToken)"/>: deserializes the
    /// latest result payload to <typeparamref name="TResult"/> via the registered serializer, or returns
    /// <c>default</c> when the job has produced no result. Non-blocking (does not wait for completion).
    /// </summary>
    ValueTask<TResult?> GetResultAsync<TResult>(JobLookup lookup, CancellationToken ct = default);

    /// <summary>
    /// Cancel the job identified by <paramref name="lookup"/>: any non-terminal status moves to
    /// <c>Cancelled</c>, and the cancel cascades recursively to the job's non-terminal descendant
    /// subtree (descendants land <c>Cancelled</c> with <c>reason = ParentCancelled</c>). Stamps
    /// <c>actor = Operator</c>, <c>reason = ControlManual</c> on the target;
    /// <paramref name="reasonMessage"/> is persisted on the row and the audit event. <paramref name="actorKey"/> is
    /// recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when
    /// unknown. An already-terminal job is <see cref="JobControlAction.Rejected"/>; a missing job is
    /// <see cref="JobControlAction.NotFound"/>.
    /// </summary>
    ValueTask<JobControlResult> CancelAsync(
        JobLookup lookup,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Pause the job identified by <paramref name="lookup"/>: a job at or before <c>Ready</c> moves to
    /// <c>Paused</c>. Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>;
    /// <paramref name="reasonMessage"/> is persisted on the row and the audit event. <paramref name="actorKey"/> is
    /// recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when
    /// unknown. A running or terminal job is <see cref="JobControlAction.Rejected"/>. <see
    /// cref="RescheduleAsync"/> re-arms a <c>Paused</c> job to <c>Ready</c>, a documented path out of
    /// <c>Paused</c>.
    /// </summary>
    ValueTask<JobControlResult> PauseAsync(
        JobLookup lookup,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Resume the job identified by <paramref name="lookup"/>: a <c>Paused</c> job moves to <c>Ready</c>
    /// to run now. Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>;
    /// <paramref name="reasonMessage"/> is recorded on the audit event only (the row's reason is
    /// cleared). <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the
    /// authenticated principal name); null when unknown. A non-paused job is <see cref="JobControlAction.Rejected"/>.
    /// </summary>
    ValueTask<JobControlResult> ResumeAsync(
        JobLookup lookup,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Restart the job identified by <paramref name="lookup"/>: any status except <c>Executing</c> moves
    /// to <c>Ready</c> to run now. Resets the failure budget and clears the retention deadline; the
    /// attempt counter is unchanged. Stamps <c>actor = Operator</c>, <c>reason = ControlManual</c>;
    /// <paramref name="reasonMessage"/> is recorded on the audit event only. <paramref name="actorKey"/> is
    /// recorded on the audit event as the operator identity (e.g. the authenticated principal name); null when
    /// unknown. An executing job is <see cref="JobControlAction.Rejected"/>.
    /// </summary>
    ValueTask<JobControlResult> RestartAsync(
        JobLookup lookup,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Move the job identified by <paramref name="lookup"/>'s next-run instant to
    /// <paramref name="nextRunAtUtc"/>. Applies to Paused/Suspended/Ready rows (a paused or suspended
    /// row is re-armed Ready so the new instant actually fires); an in-flight or terminal row is
    /// rejected. The transition is audited (job.rescheduled); <paramref name="reasonMessage"/> is
    /// persisted on the audit event and <paramref name="actorKey"/> is recorded on the audit event as
    /// the operator identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<JobControlResult> RescheduleAsync(
        JobLookup lookup,
        DateTime nextRunAtUtc,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Change the claim priority of the job identified by <paramref name="lookup"/>: any non-terminal
    /// row (including in-flight) accepts <paramref name="priority"/>, which affects only its next
    /// claim; status and cursor are unchanged. A terminal job is <see cref="JobControlAction.Rejected"/>.
    /// The transition is audited (job.reprioritized); <paramref name="reasonMessage"/> is persisted on
    /// the audit event and <paramref name="actorKey"/> is recorded on the audit event as the operator
    /// identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<JobControlResult> ReprioritizeAsync(
        JobLookup lookup,
        JobPriorityCode priority,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Hard-delete the terminal job identified by <paramref name="lookup"/>: deletes its <c>events</c>
    /// and <c>alerts</c> rows, then the job row itself (CASCADEs to its
    /// runtime/schedule/step/result/checkpoint/tag rows). Only a terminal job (<c>Done</c>/<c>Failed</c>/
    /// <c>Cancelled</c>) may be purged; a non-terminal job is <see cref="JobControlAction.Rejected"/>, and
    /// so is a terminal job that has child jobs (deleting it would orphan the child's lineage - <c>parent_id</c>
    /// carries no DB cascade). Always emits <c>job.purged</c> (not audit-gated), with <c>job_id</c>/<c>job_ref</c>
    /// null on that row and the purged job's ref and name recorded on its <c>ReasonMessage</c> instead.
    /// <paramref name="actorKey"/> is recorded on the audit event as the operator identity (e.g. the
    /// authenticated principal name); null when unknown. Unlike the other control verbs there is no
    /// caller reason message: purge carries no context beyond the audit trail itself.
    /// </summary>
    ValueTask<JobControlResult> PurgeAsync(JobLookup lookup, string? actorKey = null, CancellationToken ct = default);

    /// <summary>
    /// Apply <paramref name="action"/> to every job in <paramref name="targets"/>, positionally: the
    /// returned list's element <c>i</c> is the outcome for <c>targets[i]</c>. Loops over the single-job
    /// control verb rather than a dedicated batch routine - control verbs are low-frequency operator
    /// actions on tens-to-hundreds of rows, each single verb is one already-proven round trip, and a
    /// TVP/array batch routine for seven verbs would triple the SQL surface for no operator-visible
    /// latency win at these volumes. Per-item independence (one <see cref="JobControlAction.Rejected"/>
    /// doesn't disturb siblings) falls out of the loop for free; cancelling <paramref name="ct"/> mid-loop
    /// abandons the remaining targets and discards completed items' results, though already-applied
    /// transitions stay applied. <paramref name="options"/>.NextRunAtUtc is required when <paramref
    /// name="action"/> is <see cref="JobBatchAction.Reschedule"/> and <paramref name="options"/>.Priority
    /// is required when <see cref="JobBatchAction.Reprioritize"/> (both throw <see cref="ArgumentException"/>
    /// otherwise, before any target is touched); other actions ignore irrelevant option fields.
    /// <paramref name="targets"/> is capped at 1000 entries (throws otherwise). <paramref name="actorKey"/>
    /// is recorded on each audit event as the operator identity; null when unknown.
    /// </summary>
    ValueTask<IReadOnlyList<JobControlResult>> ControlBatchAsync(
        JobBatchAction action,
        IReadOnlyList<JobLookup> targets,
        JobBatchOptions? options = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Raise the presence-only signal <paramref name="name"/> on the job identified by
    /// <paramref name="job"/>. Sets the <c>(job_id, name)</c> slot to <c>Set</c> (last-writer-wins) and,
    /// when the job is <c>Suspended</c> on a matching <c>ctx.WaitSignalAsync</c>, moves it to
    /// <c>Ready</c> to run now. A <c>Paused</c> job stays paused (the signal is still recorded). A
    /// terminal job is <see cref="JobControlAction.Rejected"/> and no slot is written; a missing job is
    /// <see cref="JobControlAction.NotFound"/>. <paramref name="actorKey"/> is recorded on the audit event
    /// as the operator identity (e.g. the authenticated principal name); null when unknown. Unlike the
    /// other control verbs, <paramref name="actorKey"/> trails <paramref name="ct"/> here: this overload
    /// shares an argument count with <see cref="RaiseSignalAsync{T}"/> once <c>T</c> is inferred as
    /// <c>string</c>, and putting a same-typed <c>string?</c> parameter in the same slot as the other
    /// overload's <c>value</c> would make ordinary 3-argument calls bind to the wrong overload.
    /// </summary>
    ValueTask<JobControlResult> RaiseSignalAsync(JobLookup job, string name, CancellationToken ct = default, string? actorKey = null);

    /// <summary>
    /// Typed-payload variant of
    /// <see cref="RaiseSignalAsync(JobLookup, string, CancellationToken, string?)"/>. <paramref name="value"/> is
    /// JSON-serialized and stored on the slot; a handler reads it via
    /// <c>ctx.WaitSignalAsync&lt;T&gt;(name)</c>. <paramref name="actorKey"/> is recorded on the audit event
    /// as the operator identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<JobControlResult> RaiseSignalAsync<T>(
        JobLookup job,
        string name,
        T value,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Pre-formed-payload variant of
    /// <see cref="RaiseSignalAsync(JobLookup, string, CancellationToken, string?)"/>. Stores
    /// <paramref name="value"/> on the slot verbatim under its <c>Format</c>; a handler reads it via
    /// <c>ctx.WaitSignalAsync&lt;T&gt;(name)</c>. <see cref="JobPayload.None"/> raises a presence-only
    /// signal. The HTTP signal endpoint uses this overload to pass a request body through as a JSON
    /// payload without binding the handler's type. <paramref name="actorKey"/> is recorded on the audit event
    /// as the operator identity (e.g. the authenticated principal name); null when unknown.
    /// </summary>
    ValueTask<JobControlResult> RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>List jobs newest first, optionally filtered by namespace, status, definition, tenant, correlation id, or tags.</summary>
    ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default);

    /// <summary>List audit events newest first, optionally scoped to a job, lineage, namespace, or event code.</summary>
    ValueTask<PagedResult<JobEventListItem>> ListJobEventsAsync(ListJobEventsQuery query, CancellationToken ct = default);

    /// <summary>One-shot dashboard health counters, optionally scoped to a namespace.</summary>
    ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default);

    /// <summary>List registered namespace names alphabetically, optionally restricted to a name prefix.</summary>
    ValueTask<PagedResult<string>> ListNamespacesAsync(ListNamespacesQuery query, CancellationToken ct = default);

    /// <summary>Schedules domain (pause/resume/list). See <see cref="ISchedules"/>.</summary>
    ISchedules Schedules { get; }

    /// <summary>Job definitions domain (overrides/detail/list). See <see cref="IDefinitions"/>.</summary>
    IDefinitions Definitions { get; }

    /// <summary>Workers domain (list). See <see cref="IWorkers"/>.</summary>
    IWorkers Workers { get; }

    /// <summary>Alerts domain (list). See <see cref="IAlerts"/>.</summary>
    IAlerts Alerts { get; }

    /// <summary>Tenants domain (register/list). See <see cref="ITenants"/>.</summary>
    ITenants Tenants { get; }

    /// <summary>Namespaces domain (list/suspend/resume/metadata). See <see cref="INamespaces"/>.</summary>
    INamespaces Namespaces { get; }

    /// <summary>Exact searchable metadata attachments. See <see cref="ITags"/>.</summary>
    ITags Tags { get; }

    /// <summary>The durable provider backing this runtime (surfaced by the capabilities read).</summary>
    DbProvider Provider { get; }
}
