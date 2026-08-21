using System.Data.Common;
using System.Globalization;
using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.ChildLatches;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Modules.Execution.Workers;
using Acta.Runtime.Querying;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Jobs;

/// <summary>
/// Jobs feature read behavior: job dispatch (id passthrough, ref and deduplication-key resolves
/// with canonicalization), the read projections (explanation narrative against the database clock,
/// lineage truncation, result payload decode), and the job-list validation and cursor math.
/// </summary>
internal sealed class JobsService(
    IJobStore store,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    Acta.Runtime.Modules.Execution.Signals.ISignalStore signalStore,
    IScheduleStore scheduleStore,
    IExecutionStore executionStore,
    WorkerWakeupPublisher wakeupPublisher,
    IOptions<JobsOptions> options
) : IExecutionQueries
{
    internal const int MaxBatchRows = 5000;
    internal const long MaxBatchPayloadBytes = 64L * 1024 * 1024;

    private readonly int _maxInlinePayloadBytes = options.Value.MaxInlinePayloadBytes;

    private const string OrderCreatedDesc = "created_at_utc desc, id desc";
    private const string ListOperationName = "ListJobs";

    public ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct) =>
        job.Kind switch
        {
            JobLookupKind.JobId => ValueTask.FromResult<long?>(job.JobId),
            JobLookupKind.JobRef => store.ResolveJobIdByRefAsync(job.JobRef!.Value, ct),
            JobLookupKind.DeduplicationKey => store.ResolveJobIdByDeduplicationKeyAsync(
                IdentifierSyntax.CanonicalizeKebab(job.JobNamespace!, nameof(job.JobNamespace)),
                IdentifierSyntax.NormalizeKeyLookup(job.DeduplicationKey!, nameof(job.DeduplicationKey)),
                ct
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(job), job.Kind, "Unsupported job job kind."),
        };

    public async ValueTask<JobDetail?> GetAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        return jobId is null ? null : await store.GetJobAsync(jobId.Value, ct);
    }

    public async ValueTask<JobStatusCode?> GetStatusAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        return jobId is null ? null : await store.GetJobStatusAsync(jobId.Value, ct);
    }

    public async ValueTask<JobExplanation?> ExplainAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return null;
        }

        var data = await store.GetJobExplanationAsync(jobId.Value, ct);
        if (data is null)
        {
            return null;
        }

        // Read the DB clock so the timing narrative ("lease expired 2m ago") measures against the same
        // clock that stamped the rows, not this process's wall clock.
        var nowUtc = await clock.GetUtcNowAsync(ct);
        return JobExplainer.Explain(data, nowUtc);
    }

    public async ValueTask<JobLineageMap?> GetLineageMapAsync(JobLookup job, JobLineageMapOptions? options, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return null;
        }

        // Clamp the child limit and fetch one extra so the mapper can flag a truncated set from the tail.
        var childLimit = Math.Clamp(options?.ChildLimit ?? 100, 1, 1000);
        var data = await store.GetJobLineageMapAsync(jobId.Value, childLimit + 1, ct);
        return data is null ? null : JobLineageMapper.Map(data, childLimit);
    }

    public async ValueTask<JobPayload?> GetInputAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return null;
        }

        var record = await store.GetJobInputAsync(jobId.Value, ct);
        // A None-format row carries no input; surface it as null like a missing job.
        return record is null || record.FormatId == 0
            ? null
            : JobPayload.FromBytes(JobPayloadFormat.ForId(record.FormatId), record.Data.ToArray());
    }

    public async ValueTask<JobPayload?> GetResultAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return null;
        }

        var record = await store.GetJobResultAsync(jobId.Value, executionNumber: null, ct);
        return record is null ? null : JobPayload.FromBytes(record.Format, record.Data.ToArray());
    }

    public async ValueTask<IReadOnlyList<JobCheckpointItem>> GetCheckpointsAsync(JobLookup job, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        return jobId is null ? [] : await store.GetJobCheckpointsAsync(jobId.Value, ct);
    }

    public async ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct)
    {
        var payload = await GetResultAsync(job, ct);
        return payload is { } p ? serializers.Resolve(p.Format.Id).Deserialize<TResult>(p) : default;
    }

    public async ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = JobsQueryLimits.NormalizePageSize(query.PageSize);
        query = query with
        {
            JobNamespace = QueryValidation.ValidateNamespace(query.JobNamespace, nameof(query.JobNamespace)),
            // RHS reads the source instance (pre-fold); ValidateJobName only null-checks the namespace, so the pre-fold value is fine.
            JobName = QueryValidation.ValidateJobName(query.JobName, query.JobNamespace, nameof(query.JobName)),
        };
        QueryValidation.ValidateEnum(query.Status, nameof(query.Status));
        QueryValidation.ValidatePositiveId(query.ParentJobId, nameof(query.ParentJobId));
        QueryValidation.ValidatePositiveId((long?)query.TenantId, nameof(query.TenantId));
        var tenantKey = string.IsNullOrWhiteSpace(query.TenantKey)
            ? null
            : IdentifierSyntax.NormalizeKeyLookup(query.TenantKey, nameof(query.TenantKey));
        var tagFiltersJson = TagFilterJson.Normalize(query.Tags, nameof(ListJobsQuery));
        // Tri-state flags: only true restricts, so false folds to null before hashing and binding.
        var terminalOnly = query.TerminalOnly == true ? (bool?)true : null;
        var recurringOnly = query.RecurringOnly == true ? (bool?)true : null;

        var filterHash = QueryFilterHash.Compute([
            ("ns", query.JobNamespace),
            ("status", Num(query.Status)),
            ("name", query.JobName),
            ("parent", Num(query.ParentJobId)),
            ("tenant", query.TenantId?.ToString(CultureInfo.InvariantCulture)),
            ("tenantKey", tenantKey),
            ("correlation", query.CorrelationKey),
            ("tags", tagFiltersJson),
            ("terminal", terminalOnly is null ? null : "1"),
            ("recurring", recurringOnly is null ? null : "1"),
        ]);

        DateTime? cursorCreatedAtUtc = null;
        long? cursorId = null;
        if (query.Cursor is not null)
        {
            var keys = PageCursorCodec.Decode(
                query.Cursor,
                ListOperationName,
                OrderCreatedDesc,
                filterHash,
                [CursorKeyKind.Utc, CursorKeyKind.Long]
            );
            cursorCreatedAtUtc = (DateTime)keys[0];
            cursorId = (long)keys[1];
        }

        // One round trip returns the keyset page and, when asked, the filter-wide count (two result
        // sets); the count statement short-circuits to NULL and runs no scan when IncludeTotal is false.
        var page = await store.ListJobsAsync(
            new JobPageRequest(
                query.JobNamespace,
                query.Status,
                query.JobName,
                query.ParentJobId,
                query.TenantId,
                tenantKey,
                query.CorrelationKey,
                tagFiltersJson,
                terminalOnly,
                recurringOnly,
                cursorCreatedAtUtc,
                cursorId,
                pageSize + 1,
                query.IncludeTotal
            ),
            ct
        );

        var rows = page.Rows;
        var hasMore = rows.Count > pageSize;
        var items = hasMore ? rows.Take(pageSize).ToList() : rows;

        var nextCursor = hasMore
            ? PageCursorCodec.Encode(ListOperationName, OrderCreatedDesc, filterHash, [items[^1].CreatedAtUtc, items[^1].JobId])
            : null;

        return new PagedResult<JobListItem>(items, nextCursor, hasMore, pageSize, page.Total);
    }

    // Acta-owned single enqueue: shares the whole pipeline with the caller-transaction twin and, unlike
    // it, publishes the post-enqueue wakeup.
    public ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct) =>
        EnqueueOneCoreAsync(request, (row, jobRef, token) => store.EnqueueOneAsync(row, jobRef, token), publishWake: true, ct);

    // Caller-transaction single enqueue: same normalization, size, canonicalization, validation, and
    // exception translation as the owned path, but inserts through the supplied transaction and never
    // wakes a worker (Acta cannot know whether or when the caller commits).
    public ValueTask<JobEnqueueOutcome> EnqueueInTransactionAsync(
        DbTransaction transaction,
        JobEnqueueRequest request,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EnqueueOneCoreAsync(
            request,
            (row, jobRef, token) => store.EnqueueOneInTransactionAsync(transaction, row, jobRef, token),
            publishWake: false,
            ct
        );
    }

    private async ValueTask<JobEnqueueOutcome> EnqueueOneCoreAsync(
        JobEnqueueRequest request,
        Func<JobEnqueueRow, Guid, CancellationToken, Task<IReadOnlyList<EnqueueOutcomeRow>>> execute,
        bool publishWake,
        CancellationToken ct
    )
    {
        request = JobEnqueueRequestValidation.NormalizeAndValidate(request, nameof(request));
        EnsureInlineSize("enqueue input", request.Input);

        var row = JobEnqueueRows.Canonicalize(ToRow(request));
        JobEnqueueRows.ValidateRow(row, 0);
        var jobRef = JobRef.New().Value;

        IReadOnlyList<EnqueueOutcomeRow> rows;
        try
        {
            rows = await execute(row, jobRef, ct);
        }
        catch (DbException ex)
        {
            if (TryTranslateEnqueue(ex) is { } rejected)
            {
                throw rejected;
            }
            throw;
        }

        if (rows.Count != 1)
        {
            throw new InvalidOperationException(
                $"Enqueue returned {rows.Count} outcomes for one input row. The enqueue_one routine must produce exactly one outcome."
            );
        }

        var outcome = new JobEnqueueOutcome(rows[0].JobId, new JobRef(rows[0].JobRef), rows[0].Action);
        if (publishWake && EnqueueWakeReason(request, outcome.Action) is { } reason)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(request.JobNamespace), reason, ct);
        }

        return outcome;
    }

    // Acta-owned batch enqueue: publishes one wakeup per distinct due namespace.
    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(IReadOnlyList<JobEnqueueRequest> requests, CancellationToken ct) =>
        EnqueueBatchCoreAsync(
            requests,
            r => EnqueueAsync(r, ct),
            (rows, jobRefs, token) => store.EnqueueBatchAsync(rows, jobRefs, token),
            publishWake: true,
            ct
        );

    // Caller-transaction batch enqueue: the whole batch inserts through the supplied transaction and no
    // wakeup is published.
    public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchInTransactionAsync(
        DbTransaction transaction,
        IReadOnlyList<JobEnqueueRequest> requests,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return EnqueueBatchCoreAsync(
            requests,
            r => EnqueueInTransactionAsync(transaction, r, ct),
            (rows, jobRefs, token) => store.EnqueueBatchInTransactionAsync(transaction, rows, jobRefs, token),
            publishWake: false,
            ct
        );
    }

    private async ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchCoreAsync(
        IReadOnlyList<JobEnqueueRequest> requests,
        Func<JobEnqueueRequest, ValueTask<JobEnqueueOutcome>> executeOne,
        Func<IReadOnlyList<JobEnqueueRow>, IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyList<EnqueueOutcomeRow>>> executeBatch,
        bool publishWake,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return [];
        }

        // One row dispatches to the scalar path; the outcomes are contractually identical.
        if (requests.Count == 1)
        {
            return [await executeOne(requests[0])];
        }

        if (requests.Count > MaxBatchRows)
        {
            throw new ArgumentException(
                $"Enqueue batch size {requests.Count} exceeds the {MaxBatchRows}-row limit. " + "Split the batch caller-side and retry.",
                nameof(requests)
            );
        }

        var normalizedRequests = new JobEnqueueRequest[requests.Count];
        var rows = new JobEnqueueRow[requests.Count];
        long payloadBytes = 0;
        for (var i = 0; i < requests.Count; i++)
        {
            var request = JobEnqueueRequestValidation.NormalizeAndValidate(requests[i], $"{nameof(requests)}[{i}]");
            EnsureInlineSize("enqueue input", request.Input);
            normalizedRequests[i] = request;
            var row = JobEnqueueRows.Canonicalize(ToRow(request));
            JobEnqueueRows.ValidateRow(row, i);
            rows[i] = row;
            payloadBytes += row.Input.Data.Length;
        }

        if (payloadBytes > MaxBatchPayloadBytes)
        {
            throw new ArgumentException(
                $"Enqueue batch carries {payloadBytes} payload bytes, over the {MaxBatchPayloadBytes}-byte limit. "
                    + "Split the batch caller-side and retry.",
                nameof(requests)
            );
        }

        ValidateDeduplicationKeyUniqueness(rows);

        // The public ref is allocated here, in C#, never by the database; the routine writes it onto
        // the inserted row and echoes it back (a deduplicated row returns its existing ref instead).
        var jobRefs = new Guid[rows.Length];
        for (var i = 0; i < jobRefs.Length; i++)
        {
            jobRefs[i] = JobRef.New().Value;
        }

        IReadOnlyList<EnqueueOutcomeRow> outcomeRows;
        try
        {
            outcomeRows = await executeBatch(rows, jobRefs, ct);
        }
        catch (DbException ex)
        {
            if (TryTranslateEnqueue(ex) is { } rejected)
            {
                throw rejected;
            }
            throw;
        }

        if (outcomeRows.Count != rows.Length)
        {
            throw new InvalidOperationException(
                $"Enqueue batch returned {outcomeRows.Count} outcomes for {rows.Length} input rows. "
                    + "The enqueue_batch routine must produce exactly one outcome per input ordinal."
            );
        }

        var outcomes = new JobEnqueueOutcome[rows.Length];
        foreach (var row in outcomeRows)
        {
            outcomes[row.Ordinal] = new JobEnqueueOutcome(row.JobId, new JobRef(row.JobRef), row.Action);
        }

        if (!publishWake)
        {
            return outcomes;
        }

        // One wake per distinct namespace in the batch; a due-now row's WorkAvailable outranks a
        // delayed row's HorizonChanged for the same namespace (the wake itself is identical; the
        // reason is metrics-only).
        Dictionary<string, WorkerWakeupReason>? wakes = null;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var request = normalizedRequests[i];
            if (EnqueueWakeReason(request, outcomes[i].Action) is { } reason)
            {
                wakes ??= new Dictionary<string, WorkerWakeupReason>(StringComparer.Ordinal);
                if (!wakes.TryGetValue(request.JobNamespace, out var existing) || existing != WorkerWakeupReason.WorkAvailable)
                {
                    wakes[request.JobNamespace] = reason;
                }
            }
        }

        if (wakes is not null)
        {
            foreach (var (ns, reason) in wakes)
            {
                await wakeupPublisher.WakeAsync(WorkerWakeupChannel.WorkerNamespace(ns), reason, ct);
            }
        }

        return outcomes;
    }

    public async ValueTask<JobControlResult> CancelAsync(JobLookup job, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return new JobControlResult(0, ControlAction.NotFound, null);
        }

        var cancel = await store.CancelJobAsync(jobId.Value, Input(reasonMessage, actorKey), ct);
        var result = new JobControlResult(jobId.Value, (ControlAction)(byte)cancel.Outcome.Action, cancel.Outcome.Status);

        if (result.Action == ControlAction.Applied)
        {
            await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(result.JobId), WorkerWakeupReason.JobFinished, ct);
            if (
                cancel.ParentId is { } parentId
                && await RaiseChildLatch.Run(signalStore, jobId.Value, parentId, JobStatusCode.Cancelled, ct)
            )
            {
                await wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
            }
        }

        if (result.Status == JobStatusCode.Cancelled)
        {
            foreach (
                var cancelledId in await CancelDescendants.Run(executionStore, store, jobId.Value, CancelDescendants.ParentCancelled, ct)
            )
            {
                await wakeupPublisher.WakeAsync(WorkerWakeupChannel.JobCompletion(cancelledId), WorkerWakeupReason.JobFinished, ct);
            }
        }

        return result;
    }

    public ValueTask<JobControlResult> PauseAsync(JobLookup job, string? reasonMessage, string? actorKey, CancellationToken ct) =>
        ApplyControlAsync(job, (id, c) => store.PauseJobAsync(id, Input(reasonMessage, actorKey), c), ct);

    // Resume and restart are recurring-aware: a recurring slot recomputes its misfire-aware slot MIN
    // (run-now for non-recurring jobs). A recurring slot whose schedules all yield no upcoming run is
    // rejected rather than resumed/restarted to run-now; restart must not resurrect a removed slot.
    public async ValueTask<JobControlResult> ResumeAsync(JobLookup job, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        var result = await ApplyControlAsync(
            job,
            async (id, c) =>
            {
                var (reject, nextRun) = await ResolveRecurringNextRunAsync(id, c);
                return reject
                    ? new JobControlOutcome(JobControlActionInternal.Rejected, JobStatusCode.Paused)
                    : await store.ResumeJobAsync(id, Input(reasonMessage, actorKey), nextRun, c);
            },
            ct
        );
        await PublishControlWakeAsync(result, ct);
        return result;
    }

    public async ValueTask<JobControlResult> RestartAsync(JobLookup job, string? reasonMessage, string? actorKey, CancellationToken ct)
    {
        var result = await ApplyControlAsync(
            job,
            async (id, c) =>
            {
                var (reject, nextRun) = await ResolveRecurringNextRunAsync(id, c);
                return reject
                    ? new JobControlOutcome(JobControlActionInternal.Rejected, JobStatusCode.Paused)
                    : await store.RestartJobAsync(id, Input(reasonMessage, actorKey), nextRun, c);
            },
            ct
        );
        await PublishControlWakeAsync(result, ct);
        return result;
    }

    public async ValueTask<JobControlResult> RescheduleAsync(
        JobLookup job,
        DateTime nextRunAtUtc,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(nextRunAtUtc, DateTime.MinValue, nameof(nextRunAtUtc));
        var result = await ApplyControlAsync(
            job,
            (id, c) => store.RescheduleJobAsync(id, nextRunAtUtc, Input(reasonMessage, actorKey), c),
            ct
        );
        await PublishControlWakeAsync(result, ct);
        return result;
    }

    public async ValueTask<JobControlResult> ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        if (!Enum.IsDefined(priority))
        {
            throw new ArgumentOutOfRangeException(nameof(priority));
        }

        var result = await ApplyControlAsync(
            job,
            (id, c) => store.ReprioritizeJobAsync(id, priority, Input(reasonMessage, actorKey), c),
            ct
        );
        await PublishControlWakeAsync(result, ct);
        return result;
    }

    public ValueTask<JobControlResult> UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    )
    {
        EnsureInlineSize("update input", input);
        return ApplyControlAsync(job, (id, c) => store.UpdateJobInputAsync(id, input, Input(reasonMessage, actorKey), c), ct);
    }

    public ValueTask<JobControlResult> PurgeAsync(JobLookup job, string? actorKey, CancellationToken ct) =>
        ApplyControlAsync(job, (id, c) => store.PurgeJobAsync(id, Input(null, actorKey), c), ct);

    public Task ResetJobStateAsync(long jobId, CancellationToken ct) => store.ResetJobStateAsync(jobId, ct);

    // Enqueue guards raise provider exceptions whose message begins with a stable ACTA:ENQ_* token
    // (sqlite RAISE carries only text, so the discriminator lives in the message). Tokens are matched
    // by substring because provider wrappers may prepend context (e.g. "SQLite Error N:").
    private static readonly (string Token, EnqueueRejectionReason Reason)[] EnqueueRejectionTokens =
    [
        ("ACTA:ENQ_NS_SUSPENDED:", EnqueueRejectionReason.NamespaceSuspended),
        ("ACTA:ENQ_TENANT_SUSPENDED:", EnqueueRejectionReason.TenantSuspended),
        ("ACTA:ENQ_TENANT_UNKNOWN:", EnqueueRejectionReason.TenantUnknown),
        ("ACTA:ENQ_ROUTE_UNKNOWN:", EnqueueRejectionReason.RouteUnknown),
        ("ACTA:ENQ_DEF_RETIRED:", EnqueueRejectionReason.DefinitionRetired),
        ("ACTA:ENQ_TENANT_REQUIRED:", EnqueueRejectionReason.TenantRequired),
        ("ACTA:ENQ_TENANT_FORBIDDEN:", EnqueueRejectionReason.TenantForbidden),
        ("ACTA:ENQ_TENANT_MISMATCH:", EnqueueRejectionReason.TenantMismatch),
    ];

    private static EnqueueRejectedException? TryTranslateEnqueue(DbException ex)
    {
        foreach (var (token, reason) in EnqueueRejectionTokens)
        {
            var idx = ex.Message.IndexOf(token, StringComparison.Ordinal);
            if (idx >= 0)
            {
                return new EnqueueRejectedException(reason, ex.Message[(idx + token.Length)..], ex);
            }
        }
        return null;
    }

    // The enqueue publish rule: a request schedules ahead of now when it carries a positive delay or
    // any absolute run time; otherwise it is due now and wakes WorkAvailable whether inserted or
    // deduplicated. A scheduled-ahead INSERT wakes HorizonChanged; a scheduled-ahead dedupe changes
    // nothing, so no wake.
    private static WorkerWakeupReason? EnqueueWakeReason(JobEnqueueRequest request, JobEnqueueAction action)
    {
        var scheduledAhead = request.DelaySeconds is > 0 || request.NextRunAtUtc is not null;
        return !scheduledAhead ? WorkerWakeupReason.WorkAvailable
            : action == JobEnqueueAction.Inserted ? WorkerWakeupReason.HorizonChanged
            : null;
    }

    private static JobEnqueueRow ToRow(JobEnqueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new JobEnqueueRow(
            NamespaceName: request.JobNamespace,
            JobName: request.JobName,
            Input: request.Input,
            PriorityOverride: request.Priority,
            DeduplicationKey: request.DeduplicationKey,
            CorrelationKey: request.CorrelationKey,
            Tags: request.Tags,
            ExclusiveKey: request.ExclusiveKey,
            NextRunAtUtc: request.NextRunAtUtc,
            DelaySeconds: request.DelaySeconds,
            ParentId: request.ParentJobId,
            TenantKey: request.TenantKey,
            OverrideParentTenant: request.OverrideParentTenant
        );
    }

    // Same-batch duplicate DeduplicationKeys never reach the dedup logic in the routine (it only matches
    // rows already in the table), so the providers diverge: SQL Server trips the unique index and
    // throws, Postgres skips one row via ON CONFLICT and returns a null job_id. Reject in C# before
    // any SQL so the outcome is identical everywhere. The dedup scope mirrors the unique indexes: per
    // namespace for roots, per direct parent for children.
    internal static void ValidateDeduplicationKeyUniqueness(IReadOnlyList<JobEnqueueRow> rows)
    {
        if (rows.Count < 2)
        {
            return;
        }

        var seen = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var deduplicationKey = row.DeduplicationKey;
            if (deduplicationKey is null)
            {
                continue;
            }

            var scope = row.ParentId is { } parentId
                ? $"child:{parentId.ToString(CultureInfo.InvariantCulture)}"
                : $"root:{row.NamespaceName}";
            var composite = scope + "\0" + deduplicationKey;

            if (seen.TryGetValue(composite, out var firstOrdinal))
            {
                throw row.ParentId is { } duplicateParentId
                    ? DuplicateDeduplicationKeyInBatchException.ForChild(duplicateParentId, deduplicationKey, firstOrdinal, i)
                    : DuplicateDeduplicationKeyInBatchException.ForRoot(row.NamespaceName, deduplicationKey, firstOrdinal, i);
            }

            seen.Add(composite, i);
        }
    }

    private void EnsureInlineSize(string entryPoint, JobPayload payload)
    {
        var length = payload.Data.Length;
        if (length > _maxInlinePayloadBytes)
        {
            throw new PayloadTooLargeException(entryPoint, length, _maxInlinePayloadBytes);
        }
    }

    // The public control surface is operator/manual only: the actor (Operator) and causal reason
    // (ControlManual) are stamped here, never accepted from the caller. reason_message is capped to
    // the column's declared length so an over-length operator message is capped identically on both
    // providers.
    private static JobControlInput Input(string? msg, string? actorKey) =>
        new(Operator(actorKey), Acta.JobEventReasonCode.JobControlManual, msg.Truncate(ActaTextLimits.ReasonMessage));

    private static JobControlActor Operator(string? actorKey) =>
        new(ActorCode.Operator, JobControlActor.SanitizeActorKey(actorKey).Truncate(ActaTextLimits.ActorKey));

    private async ValueTask<JobControlResult> ApplyControlAsync(
        JobLookup job,
        Func<long, CancellationToken, Task<JobControlOutcome>> invoke,
        CancellationToken ct
    )
    {
        var jobId = await GetJobIdAsync(job, ct);
        if (jobId is null)
        {
            return new JobControlResult(0, ControlAction.NotFound, null);
        }

        var result = await invoke(jobId.Value, ct);
        return new JobControlResult(jobId.Value, (ControlAction)(byte)result.Action, result.Status);
    }

    private ValueTask PublishControlWakeAsync(JobControlResult result, CancellationToken ct) =>
        result is { Action: ControlAction.Applied, Status: JobStatusCode.Ready }
            ? wakeupPublisher.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct)
            : ValueTask.CompletedTask;

    private async Task<(bool Reject, DateTime? NextRun)> ResolveRecurringNextRunAsync(long id, CancellationToken ct)
    {
        var live = await scheduleStore.GetLiveSchedulesAsync(id, ct);
        if (live.Count == 0)
        {
            return (false, null);
        }

        var nowUtc = await clock.GetUtcNowAsync(ct);
        var slotMin = ScheduleWalker.RecomputeSlotMin(live, nowUtc);
        return slotMin is null ? (true, null) : (false, slotMin);
    }

    private static string? Num<T>(T? value)
        where T : struct, Enum =>
        value is null ? null : Convert.ToInt32(value.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    private static string? Num(long? value) => value?.ToString(CultureInfo.InvariantCulture);
}
