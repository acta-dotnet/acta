using System.Data.Common;

namespace Acta.Tests.AspNetCore.Fakes;

/// <summary>
/// <see cref="IJobs"/> over one canned job, id 42 at <see cref="FoundJobRef"/>, plus every job enqueued through it. A
/// control verb applies to any ref but two: <see cref="MissingJobRef"/> reports not found and <see cref="RejectedJobRef"/>
/// rejected. An expectedVersion other than <see cref="CurrentJobVersion"/> conflicts.
/// </summary>
internal sealed class FakeJobs : IJobs
{
    /// <summary>Ref of the job the fakes know (internal id 42).</summary>
    public static readonly JobRef FoundJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000042"));

    /// <summary>Well-formed ref no fake job carries; controls report not found.</summary>
    public static readonly JobRef MissingJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000041"));

    /// <summary>Ref of the direct child the lineage read reports under <see cref="FoundJobRef"/>.</summary>
    public static readonly JobRef ChildJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000007"));

    /// <summary>Ref whose control verbs report rejected.</summary>
    public static readonly JobRef RejectedJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000043"));

    /// <summary>The version the found job carries; any other expectedVersion is a conflict.</summary>
    public const int CurrentJobVersion = 7;

    private static readonly DateTime CreatedAtUtc = new(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc);

    private readonly Dictionary<Guid, long> _enqueuedRefs = [];
    private readonly Dictionary<long, JobPayload?> _enqueuedInputs = [];
    private readonly Dictionary<string, (long Id, JobRef Ref)> _deduplicated = new(StringComparer.Ordinal);

    public FakeJobs()
    {
        Get = (job, _) => ValueTask.FromResult(Detail(job));
        GetJobId = (job, _) => ValueTask.FromResult<GetJobIdResponse?>(Resolve(job) is { } id ? new GetJobIdResponse(id) : null);
        GetInput = (job, _) => ValueTask.FromResult<GetJobInputResponse?>(Input(job) is { } input ? new GetJobInputResponse(input) : null);
        GetResult = (job, _) =>
            ValueTask.FromResult<GetResultResponse?>(
                Resolve(job) == 42 && StoredResult is { } result ? new GetResultResponse(result) : null
            );
        GetCheckpoints = (job, _) => ValueTask.FromResult(Resolve(job) == 42 ? new GetCheckpointsResponse(StoredCheckpoints) : null);
        Explain = static (job, _) => ValueTask.FromResult<GetJobExplanationResponse?>(IsFound(job) ? Explanation() : null);
        GetLineageMap = static (job, _, _) => ValueTask.FromResult<GetJobLineageMapResponse?>(IsFound(job) ? Lineage() : null);
        GetInputTemplate = InputTemplate;
        Enqueue = (request, _) => ValueTask.FromResult(Insert(request));
        Pause = (job, reason, actor, version, _) =>
            Control("pause", job, reason, actor, version, static (i, a, s, v) => new PauseJobResponse(i, a, s, v));
        Resume = (job, reason, actor, version, _) =>
            Control("resume", job, reason, actor, version, static (i, a, s, v) => new ResumeJobResponse(i, a, s, v));
        Restart = (job, reason, actor, version, _) =>
            Control("restart", job, reason, actor, version, static (i, a, s, v) => new RestartJobResponse(i, a, s, v));
        Cancel = (job, reason, actor, version, _) =>
            Control("cancel", job, reason, actor, version, static (i, a, s, v) => new CancelJobResponse(i, a, s, v));
        Reschedule = (job, nextRunAtUtc, reason, actor, version, _) =>
        {
            RescheduleCalls.Add((job.JobRef, nextRunAtUtc, reason, actor, version));
            var (jobId, action, status, current) = ResultFor(job, version);
            return ValueTask.FromResult(new RescheduleJobResponse(jobId, action, status, current));
        };
        Reprioritize = (job, priority, reason, actor, version, _) =>
        {
            ReprioritizeCalls.Add((job.JobRef, priority, reason, actor, version));
            var (jobId, action, status, current) = ResultFor(job, version);
            return ValueTask.FromResult(new ReprioritizeJobResponse(jobId, action, status, current));
        };
        UpdateJobInput = (job, input, _, _, _) => ValueTask.FromResult(Amend(job, input));
        Purge = (job, actor, _) =>
        {
            PurgeCalls.Add((job.JobRef, actor));
            var (jobId, action, status, version) = ResultFor(job);
            return ValueTask.FromResult(new PurgeJobResponse(jobId, action, status, version));
        };
        RaiseSignal = (job, name, value, actor, _) =>
        {
            SignalCalls.Add((job.JobRef, name, value.Format.Id, value.IsNone ? null : value.Data.ToArray(), actor));
            var (jobId, action, status, version) = ResultFor(job);
            return ValueTask.FromResult(new RaiseSignalResponse(jobId, action, status, version));
        };
    }

    /// <summary>Input the found job carries. The amend verb reads its format and writes the amended payload back; null
    /// is a no-input job.</summary>
    public JobPayload? StoredInput { get; set; }

    /// <summary>Result the found job has produced; null means none.</summary>
    public JobPayload? StoredResult { get; set; }

    public IReadOnlyList<GetCheckpointsItem> StoredCheckpoints { get; set; } = [];

    /// <summary>Tenant id on the found job's snapshot. Id 1 carries the key "cust-001"; any other id models a purged
    /// tenant row, an id without a key.</summary>
    public int? SnapshotTenantId { get; set; }

    public List<(string Verb, JobRef JobRef, string? Reason, string? ActorKey, int? ExpectedVersion)> ControlCalls { get; } = [];

    public List<(JobRef JobRef, DateTime NextRunAtUtc, string? Reason, string? ActorKey, int? ExpectedVersion)> RescheduleCalls { get; } =
    [];

    public List<(
        JobRef JobRef,
        JobPriorityCode Priority,
        string? Reason,
        string? ActorKey,
        int? ExpectedVersion
    )> ReprioritizeCalls { get; } = [];

    public List<(JobRef JobRef, string? ActorKey)> PurgeCalls { get; } = [];

    /// <summary>Signal raises with the delivered format and bytes; null bytes is a presence-only signal.</summary>
    public List<(JobRef JobRef, string Name, byte FormatId, byte[]? Value, string? ActorKey)> SignalCalls { get; } = [];

    /// <summary>The format-resolved payloads the amend endpoint built.</summary>
    public List<JobPayload> InputAmendCalls { get; } = [];

    public List<EnqueueJobRequest> EnqueueRequests { get; } = [];

    public Func<JobLookup, CancellationToken, ValueTask<GetJobResponse?>> Get { get; set; }

    public Func<JobLookup, CancellationToken, ValueTask<GetJobIdResponse?>> GetJobId { get; set; }

    public Func<JobLookup, CancellationToken, ValueTask<GetJobInputResponse?>> GetInput { get; set; }

    public Func<JobLookup, CancellationToken, ValueTask<GetResultResponse?>> GetResult { get; set; }

    public Func<JobLookup, CancellationToken, ValueTask<GetCheckpointsResponse?>> GetCheckpoints { get; set; }

    public Func<JobLookup, CancellationToken, ValueTask<GetJobExplanationResponse?>> Explain { get; set; }

    public Func<JobLookup, GetJobLineageMapOptions?, CancellationToken, ValueTask<GetJobLineageMapResponse?>> GetLineageMap { get; set; }

    public Func<string, string, GetInputTemplateResponse?> GetInputTemplate { get; set; }

    public Func<EnqueueJobRequest, CancellationToken, ValueTask<EnqueueJobResponse>> Enqueue { get; set; }

    public Func<JobLookup, string?, string?, int?, CancellationToken, ValueTask<PauseJobResponse>> Pause { get; set; }

    public Func<JobLookup, string?, string?, int?, CancellationToken, ValueTask<ResumeJobResponse>> Resume { get; set; }

    public Func<JobLookup, string?, string?, int?, CancellationToken, ValueTask<RestartJobResponse>> Restart { get; set; }

    public Func<JobLookup, string?, string?, int?, CancellationToken, ValueTask<CancelJobResponse>> Cancel { get; set; }

    public Func<JobLookup, DateTime, string?, string?, int?, CancellationToken, ValueTask<RescheduleJobResponse>> Reschedule { get; set; }

    public Func<
        JobLookup,
        JobPriorityCode,
        string?,
        string?,
        int?,
        CancellationToken,
        ValueTask<ReprioritizeJobResponse>
    > Reprioritize { get; set; }

    public Func<JobLookup, JobPayload, string?, string?, CancellationToken, ValueTask<UpdateJobInputResponse>> UpdateJobInput { get; set; }

    public Func<JobLookup, string?, CancellationToken, ValueTask<PurgeJobResponse>> Purge { get; set; }

    public Func<JobLookup, string, JobPayload, string?, CancellationToken, ValueTask<RaiseSignalResponse>> RaiseSignal { get; set; }

    ValueTask<GetJobResponse?> IJobs.GetAsync(JobLookup job, CancellationToken ct) => Get(job, ct);

    ValueTask<GetJobIdResponse?> IJobs.GetJobIdAsync(JobLookup job, CancellationToken ct) => GetJobId(job, ct);

    ValueTask<GetJobInputResponse?> IJobs.GetInputAsync(JobLookup job, CancellationToken ct) => GetInput(job, ct);

    ValueTask<GetResultResponse?> IJobs.GetResultAsync(JobLookup job, CancellationToken ct) => GetResult(job, ct);

    ValueTask<GetCheckpointsResponse?> IJobs.GetCheckpointsAsync(JobLookup job, CancellationToken ct) => GetCheckpoints(job, ct);

    ValueTask<GetJobExplanationResponse?> IJobs.ExplainAsync(JobLookup job, CancellationToken ct) => Explain(job, ct);

    ValueTask<GetJobLineageMapResponse?> IJobs.GetLineageMapAsync(JobLookup job, GetJobLineageMapOptions? options, CancellationToken ct) =>
        GetLineageMap(job, options, ct);

    GetInputTemplateResponse? IJobs.GetInputTemplate(string jobNamespace, string jobName) => GetInputTemplate(jobNamespace, jobName);

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync(EnqueueJobRequest request, CancellationToken ct) => Enqueue(request, ct);

    ValueTask<PauseJobResponse> IJobs.PauseAsync(
        JobLookup job,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Pause(job, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<ResumeJobResponse> IJobs.ResumeAsync(
        JobLookup job,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Resume(job, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<RestartJobResponse> IJobs.RestartAsync(
        JobLookup job,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Restart(job, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<CancelJobResponse> IJobs.CancelAsync(
        JobLookup job,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Cancel(job, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<RescheduleJobResponse> IJobs.RescheduleAsync(
        JobLookup job,
        DateTime nextRunAtUtc,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Reschedule(job, nextRunAtUtc, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<ReprioritizeJobResponse> IJobs.ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage,
        string? actorKey,
        int? expectedVersion,
        CancellationToken ct
    ) => Reprioritize(job, priority, reasonMessage, actorKey, expectedVersion, ct);

    ValueTask<UpdateJobInputResponse> IJobs.UpdateJobInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage,
        string? actorKey,
        CancellationToken ct
    ) => UpdateJobInput(job, input, reasonMessage, actorKey, ct);

    ValueTask<PurgeJobResponse> IJobs.PurgeAsync(JobLookup job, string? actorKey, CancellationToken ct) => Purge(job, actorKey, ct);

    ValueTask<RaiseSignalResponse> IJobs.RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actorKey,
        CancellationToken ct
    ) => RaiseSignal(job, name, value, actorKey, ct);

    ValueTask<GetJobStatusResponse?> IJobs.GetStatusAsync(JobLookup job, CancellationToken ct) => throw new NotSupportedException();

    ValueTask<TResult?> IJobs.GetResultAsync<TResult>(JobLookup job, CancellationToken ct)
        where TResult : default => throw new NotSupportedException();

    ValueTask<RaiseSignalResponse> IJobs.RaiseSignalAsync<T>(JobLookup job, string name, T value, string? actorKey, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(TInput input, JobEnqueueOptions? options, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync(JobContract<NoInput> job, JobEnqueueOptions? options, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<JobOutcome> IJobs.RunAndWaitAsync<TInput>(TInput input, JobExecutionOptions? options, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<JobOutcome<TResult>> IJobs.RunAndWaitAsync<TInput, TResult>(
        TInput input,
        JobExecutionOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<JobOutcome<TResult>> IJobs.RunAndWaitAsync<TInput, TResult>(
        JobContract<TInput, TResult> job,
        TInput input,
        JobExecutionOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobsResponse> IJobs.EnqueueBatchAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync(DbTransaction transaction, EnqueueJobRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobContract<TInput> job,
        TInput input,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobContract<TInput, TResult> job,
        TInput input,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobResponse> IJobs.EnqueueAsync(
        DbTransaction transaction,
        JobContract<NoInput> job,
        JobEnqueueOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    ValueTask<EnqueueJobsResponse> IJobs.EnqueueBatchAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct
    ) => throw new NotSupportedException();

    private static bool IsFound(JobLookup job) => job.JobRef == FoundJobRef || job.JobId == 42;

    private long? Resolve(JobLookup job) =>
        job.Kind == JobLookupKind.JobId ? job.JobId
        : job.JobRef == FoundJobRef ? 42
        : _enqueuedRefs.TryGetValue(job.JobRef.Value, out var id) ? id
        : null;

    private JobPayload? Input(JobLookup job) =>
        Resolve(job) switch
        {
            null => null,
            42 => StoredInput,
            var id => _enqueuedInputs.GetValueOrDefault(id.Value),
        };

    private GetJobResponse? Detail(JobLookup job)
    {
        if (IsFound(job) || (job.JobNamespace == "billing" && job.DeduplicationKey == "ck-1"))
        {
            return Snapshot(42, "billing", "send-invoice", SnapshotTenantId);
        }

        // An enqueued ref resolves to its recorded request so the aggregate detail read can compose
        // it (id = 101 + insertion index); every other ref is unknown.
        if (Resolve(job) is { } enqueuedId && enqueuedId - 101 is >= 0 and var i && i < EnqueueRequests.Count)
        {
            var request = EnqueueRequests[(int)i];
            return Snapshot(enqueuedId, request.JobNamespace, request.JobName, null);
        }

        return null;
    }

    private EnqueueJobResponse Insert(EnqueueJobRequest request)
    {
        EnqueueRequests.Add(request);

        // Second enqueue of a deduplication key matches the first, as the real store does: the
        // endpoint distinguishes created from matched, so the fake has to as well.
        if (request.DeduplicationKey is { Length: > 0 } key && _deduplicated.TryGetValue(key, out var existing))
        {
            return new EnqueueJobResponse(existing.Id, existing.Ref, EnqueueJobAction.Deduplicated);
        }

        var id = 100 + EnqueueRequests.Count;
        var jobRef = JobRef.New();
        _enqueuedRefs[jobRef.Value] = id;
        _enqueuedInputs[id] = request.Input.IsNone ? null : request.Input;
        if (request.DeduplicationKey is { Length: > 0 } fresh)
        {
            _deduplicated[fresh] = (id, jobRef);
        }

        return new EnqueueJobResponse(id, jobRef, EnqueueJobAction.Inserted);
    }

    private UpdateJobInputResponse Amend(JobLookup job, JobPayload input)
    {
        InputAmendCalls.Add(input);
        var (jobId, action, status, version) = ResultFor(job);
        if (action == ControlAction.Applied)
        {
            StoredInput = input;
        }

        return new UpdateJobInputResponse(jobId, action, status, version);
    }

    private ValueTask<T> Control<T>(
        string verb,
        JobLookup job,
        string? reason,
        string? actorKey,
        int? expectedVersion,
        Func<long, ControlAction, JobStatusCode?, int?, T> respond
    )
    {
        ControlCalls.Add((verb, job.JobRef, reason, actorKey, expectedVersion));
        var (jobId, action, status, version) = ResultFor(job, expectedVersion);
        return ValueTask.FromResult(respond(jobId, action, status, version));
    }

    private static (long JobId, ControlAction Action, JobStatusCode? Status, int? Version) ResultFor(
        JobLookup job,
        int? expectedVersion = null
    ) =>
        job.JobRef == MissingJobRef ? (0, ControlAction.NotFound, null, null)
        : expectedVersion is { } expected && expected != CurrentJobVersion
            ? (42, ControlAction.VersionConflict, JobStatusCode.Paused, CurrentJobVersion)
        : job.JobRef == RejectedJobRef ? (43, ControlAction.Rejected, JobStatusCode.Succeeded, CurrentJobVersion)
        : (42, ControlAction.Applied, JobStatusCode.Paused, CurrentJobVersion + 1);

    /// <summary>Only ("billing", "send-invoice") is known to this fake host; anything else is unregistered.</summary>
    private static GetInputTemplateResponse? InputTemplate(string jobNamespace, string jobName) =>
        jobNamespace == "billing" && jobName == "send-invoice"
            ? new GetInputTemplateResponse(
                jobNamespace,
                jobName,
                "Billing.SendInvoice",
                JobPayloadFormat.Json,
                """{"invoiceId":0,"note":null}"""
            )
            : null;

    private static GetJobResponse Snapshot(long jobId, string jobNamespace, string jobName, int? tenantId) =>
        new(
            JobId: jobId,
            JobRef: jobId == 42 ? FoundJobRef : JobRef.New(),
            LineageRootId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: "ck-1",
            CorrelationKey: null,
            JobNamespace: jobNamespace,
            JobName: jobName,
            DefinitionId: 5,
            TenantId: tenantId,
            TenantKey: tenantId == 1 ? "cust-001" : null,
            Status: JobStatusCode.Ready,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: 0,
            NextRunAtUtc: null,
            LeasedByWorkerId: null,
            LeaseExpiresAtUtc: null,
            ConcurrencyKey: null,
            RetentionUntilUtc: null,
            CreatedAtUtc: CreatedAtUtc,
            ModifiedAtUtc: CreatedAtUtc,
            LeasedByWorkerRef: null,
            Version: CurrentJobVersion
        );

    private static GetJobExplanationResponse Explanation() =>
        new(
            JobId: 42,
            JobRef: FoundJobRef,
            JobNamespace: "billing",
            JobName: "send-invoice",
            Status: JobStatusCode.Ready,
            StatusMeaning: JobStatusCode.Ready.Description,
            Headline: "Ready and eligible for claim, waiting for a worker to pick it up.",
            ActiveWait: null,
            Lease: null,
            LastExecutedBy: null,
            Steps: [],
            Reason: null,
            NextActions: [new GetJobExplanationResponse.Action("none", "no action needed - a worker will claim it")]
        );

    private static GetJobLineageMapResponse Lineage() =>
        new(
            Ancestors: [],
            Job: new GetJobLineageMapResponse.Node(
                JobId: 42,
                JobRef: FoundJobRef,
                JobNamespace: "billing",
                JobName: "send-invoice",
                Status: JobStatusCode.Ready,
                ParentJobId: null,
                ParentJobRef: null,
                LineageRootId: null,
                LineageRootJobRef: null,
                CreatedAtUtc: CreatedAtUtc,
                ModifiedAtUtc: CreatedAtUtc
            ),
            Steps: [],
            ActiveWait: null,
            Children:
            [
                new GetJobLineageMapResponse.Child(
                    JobId: 7,
                    JobRef: ChildJobRef,
                    JobName: "render-pdf",
                    Status: JobStatusCode.Executing,
                    CreatedAtUtc: new DateTime(2026, 6, 12, 6, 1, 0, DateTimeKind.Utc),
                    ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 1, 0, DateTimeKind.Utc)
                ),
            ],
            ChildrenHasMore: false
        );
}
