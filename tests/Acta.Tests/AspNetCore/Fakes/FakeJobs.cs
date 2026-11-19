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
        Get = (job, _) => Task.FromResult(Detail(job));
        GetId = (job, _) => Task.FromResult<GetJobIdResponse?>(Resolve(job) is { } id ? new GetJobIdResponse(id) : null);
        GetInput = (job, _) => Task.FromResult<GetJobInputResponse?>(Input(job) is { } input ? new GetJobInputResponse(input) : null);
        GetResult = (job, _) =>
            Task.FromResult<GetJobResultResponse?>(
                Resolve(job) == 42 && StoredResult is { } result ? new GetJobResultResponse(result) : null
            );
        ListCheckpoints = (query, _) =>
        {
            var items = Resolve(query.Job) == 42 ? StoredCheckpoints : [];
            return Task.FromResult(new ListJobCheckpointsResponse(items, null, false, 50, query.IncludeTotal ? items.Count : null));
        };
        GetCheckpointValue = (job, kind, name, _) =>
            Task.FromResult<GetJobCheckpointValueResponse?>(
                Resolve(job) == 42 && StoredCheckpointValues.TryGetValue((kind, name), out var value)
                    ? new GetJobCheckpointValueResponse(value)
                    : null
            );
        ListSteps = (query, _) =>
        {
            var items = Resolve(query.Job) == 42 ? StoredSteps : [];
            return Task.FromResult(new ListJobStepsResponse(items, null, false, 50, query.IncludeTotal ? items.Count : null));
        };
        Explain = static (job, _) => Task.FromResult<ExplainJobResponse?>(IsFound(job) ? Explanation() : null);
        GetLineageMap = static (job, _, _) => Task.FromResult<GetJobLineageMapResponse?>(IsFound(job) ? Lineage() : null);
        Enqueue = (request, _) => Task.FromResult(Insert(request));
        Pause = (job, reason, actor, version, _) =>
            Control("pause", job, reason, actor, version, JobStatusCode.Paused, static (i, a, s, v) => new PauseJobResponse(i, a, s, v));
        Resume = (job, reason, actor, version, _) =>
            Control("resume", job, reason, actor, version, JobStatusCode.Ready, static (i, a, s, v) => new ResumeJobResponse(i, a, s, v));
        Restart = (job, reason, actor, version, _) =>
            Control("restart", job, reason, actor, version, JobStatusCode.Ready, static (i, a, s, v) => new RestartJobResponse(i, a, s, v));
        Cancel = (job, reason, actor, version, _) =>
            Control("cancel", job, reason, actor, version, JobStatusCode.Cancelled, static (i, a, s, v) =>
                new CancelJobResponse(i, a, s, v)
            );
        Reschedule = (job, nextRunAtUtc, reason, actor, version, _) =>
        {
            RescheduleCalls.Add((job.JobRef, nextRunAtUtc, reason, actor, version));
            var (jobId, action, status, current) = ResultFor(job, version, JobStatusCode.Ready);
            return Task.FromResult(new RescheduleJobResponse(jobId, action, status, current));
        };
        Reprioritize = (job, priority, reason, actor, version, _) =>
        {
            ReprioritizeCalls.Add((job.JobRef, priority, reason, actor, version));
            var (jobId, action, status, current) = ResultFor(job, version, JobStatusCode.Ready);
            return Task.FromResult(new ReprioritizeJobResponse(jobId, action, status, current));
        };
        UpdateInput = (job, input, _, _, _) => Task.FromResult(Update(job, input));
        // A purge leaves no job to report; it, an input update, and a signal carry no version.
        Purge = (job, reason, actor, _) =>
        {
            PurgeCalls.Add((job.JobRef, reason, actor));
            var (jobId, action, status, _) = ResultFor(job, applied: null);
            return Task.FromResult(new PurgeJobResponse(jobId, action, status));
        };
        RaiseSignal = (job, name, value, actor, _) =>
        {
            SignalCalls.Add((job.JobRef, name, value.Format.Id, value.IsNone ? null : value.Data.ToArray(), actor));
            var (jobId, action, status, _) = ResultFor(job, applied: JobStatusCode.Ready);
            return Task.FromResult(new RaiseJobSignalResponse(jobId, action, status));
        };
        ClearSignal = (job, name, actor, _) =>
        {
            ClearSignalCalls.Add((job.JobRef, name, actor));
            var (jobId, action, status, _) = ResultFor(job, applied: JobStatusCode.Succeeded);
            // A clear refuses only a live job.
            return Task.FromResult(
                action == ControlAction.Rejected
                    ? new ClearJobSignalResponse(jobId, action, JobStatusCode.Ready, "job-not-finished")
                    : new ClearJobSignalResponse(jobId, action, status)
            );
        };
    }

    /// <summary>Input the found job carries. The update verb reads its format and writes the updated payload back; null
    /// is a no-input job.</summary>
    public JobPayload? StoredInput { get; set; }

    /// <summary>Result the found job has produced; null means none.</summary>
    public JobPayload? StoredResult { get; set; }

    public IReadOnlyList<ListJobCheckpointsItem> StoredCheckpoints { get; set; } = [];

    /// <summary>The values the found job's checkpoints hold, by kind and name; the list states each by format and size.</summary>
    public Dictionary<(CheckpointKindCode Kind, string Name), JobPayload> StoredCheckpointValues { get; } = [];

    public IReadOnlyList<ListJobStepsItem> StoredSteps { get; set; } = [];

    public List<(string Verb, JobRef JobRef, string? Reason, string? Actor, long? ExpectedVersion)> ControlCalls { get; } = [];

    public List<(JobRef JobRef, DateTime? NextRunAtUtc, string? Reason, string? Actor, long? ExpectedVersion)> RescheduleCalls { get; } =
    [];

    public List<(
        JobRef JobRef,
        JobPriorityCode Priority,
        string? Reason,
        string? Actor,
        long? ExpectedVersion
    )> ReprioritizeCalls { get; } = [];

    public List<(JobRef JobRef, string? Reason, string? Actor)> PurgeCalls { get; } = [];

    /// <summary>Signal raises with the delivered format and bytes; null bytes is a presence-only signal.</summary>
    public List<(JobRef JobRef, string Name, byte FormatId, byte[]? Value, string? Actor)> SignalCalls { get; } = [];

    public List<(JobRef JobRef, string Name, string? Actor)> ClearSignalCalls { get; } = [];

    /// <summary>The format-resolved payloads the update endpoint built.</summary>
    public List<JobPayload> InputUpdateCalls { get; } = [];

    public List<EnqueueJobRequest> EnqueueRequests { get; } = [];

    public Func<JobLookup, CancellationToken, Task<GetJobResponse?>> Get { get; set; }

    public Func<JobLookup, CancellationToken, Task<GetJobIdResponse?>> GetId { get; set; }

    public Func<JobLookup, CancellationToken, Task<GetJobInputResponse?>> GetInput { get; set; }

    public Func<JobLookup, CancellationToken, Task<GetJobResultResponse?>> GetResult { get; set; }

    public Func<ListJobCheckpointsQuery, CancellationToken, Task<ListJobCheckpointsResponse>> ListCheckpoints { get; set; }

    public Func<
        JobLookup,
        CheckpointKindCode,
        string,
        CancellationToken,
        Task<GetJobCheckpointValueResponse?>
    > GetCheckpointValue { get; set; }

    public Func<ListJobStepsQuery, CancellationToken, Task<ListJobStepsResponse>> ListSteps { get; set; }

    public Func<JobLookup, CancellationToken, Task<ExplainJobResponse?>> Explain { get; set; }

    public Func<JobLookup, GetJobLineageMapOptions?, CancellationToken, Task<GetJobLineageMapResponse?>> GetLineageMap { get; set; }

    public Func<EnqueueJobRequest, CancellationToken, Task<EnqueueJobResponse>> Enqueue { get; set; }

    public Func<JobLookup, string?, string?, long?, CancellationToken, Task<PauseJobResponse>> Pause { get; set; }

    public Func<JobLookup, string?, string?, long?, CancellationToken, Task<ResumeJobResponse>> Resume { get; set; }

    public Func<JobLookup, string?, string?, long?, CancellationToken, Task<RestartJobResponse>> Restart { get; set; }

    public Func<JobLookup, string?, string?, long?, CancellationToken, Task<CancelJobResponse>> Cancel { get; set; }

    public Func<JobLookup, DateTime?, string?, string?, long?, CancellationToken, Task<RescheduleJobResponse>> Reschedule { get; set; }

    public Func<
        JobLookup,
        JobPriorityCode,
        string?,
        string?,
        long?,
        CancellationToken,
        Task<ReprioritizeJobResponse>
    > Reprioritize { get; set; }

    public Func<JobLookup, JobPayload, string?, string?, CancellationToken, Task<UpdateJobInputResponse>> UpdateInput { get; set; }

    public Func<JobLookup, string?, string?, CancellationToken, Task<PurgeJobResponse>> Purge { get; set; }

    public Func<JobLookup, string, JobPayload, string?, CancellationToken, Task<RaiseJobSignalResponse>> RaiseSignal { get; set; }

    public Func<JobLookup, string, string?, CancellationToken, Task<ClearJobSignalResponse>> ClearSignal { get; set; }

    Task<GetJobResponse?> IJobs.GetAsync(JobLookup job, CancellationToken ct) => Get(job, ct);

    Task<GetJobIdResponse?> IJobs.GetIdAsync(JobLookup job, CancellationToken ct) => GetId(job, ct);

    Task<GetJobInputResponse?> IJobs.GetInputAsync(JobLookup job, CancellationToken ct) => GetInput(job, ct);

    Task<GetJobResultResponse?> IJobs.GetResultAsync(JobLookup job, CancellationToken ct) => GetResult(job, ct);

    Task<ListJobCheckpointsResponse> IJobs.ListCheckpointsAsync(ListJobCheckpointsQuery query, CancellationToken ct) =>
        ListCheckpoints(query, ct);

    Task<GetJobCheckpointValueResponse?> IJobs.GetCheckpointValueAsync(
        JobLookup job,
        CheckpointKindCode kind,
        string name,
        CancellationToken ct
    ) => GetCheckpointValue(job, kind, name, ct);

    Task<ListJobStepsResponse> IJobs.ListStepsAsync(ListJobStepsQuery query, CancellationToken ct) => ListSteps(query, ct);

    Task<ExplainJobResponse?> IJobs.ExplainAsync(JobLookup job, CancellationToken ct) => Explain(job, ct);

    Task<GetJobLineageMapResponse?> IJobs.GetLineageMapAsync(JobLookup job, GetJobLineageMapOptions? options, CancellationToken ct) =>
        GetLineageMap(job, options, ct);

    Task<EnqueueJobResponse> IJobs.EnqueueAsync(EnqueueJobRequest request, CancellationToken ct) => Enqueue(request, ct);

    Task<PauseJobResponse> IJobs.PauseAsync(
        JobLookup job,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Pause(job, reasonMessage, actor, expectedVersion, ct);

    Task<ResumeJobResponse> IJobs.ResumeAsync(
        JobLookup job,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Resume(job, reasonMessage, actor, expectedVersion, ct);

    Task<RestartJobResponse> IJobs.RestartAsync(
        JobLookup job,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Restart(job, reasonMessage, actor, expectedVersion, ct);

    Task<CancelJobResponse> IJobs.CancelAsync(
        JobLookup job,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Cancel(job, reasonMessage, actor, expectedVersion, ct);

    Task<RescheduleJobResponse> IJobs.RescheduleAsync(
        JobLookup job,
        DateTimeOffset? nextRunAtUtc,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Reschedule(job, nextRunAtUtc?.UtcDateTime, reasonMessage, actor, expectedVersion, ct);

    Task<ReprioritizeJobResponse> IJobs.ReprioritizeAsync(
        JobLookup job,
        JobPriorityCode priority,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => Reprioritize(job, priority, reasonMessage, actor, expectedVersion, ct);

    Task<UpdateJobInputResponse> IJobs.UpdateInputAsync(
        JobLookup job,
        JobPayload input,
        string? reasonMessage,
        string? actor,
        long? expectedVersion,
        CancellationToken ct
    ) => UpdateInput(job, input, reasonMessage, actor, ct);

    Task<PurgeJobResponse> IJobs.PurgeAsync(JobLookup job, string? reasonMessage, string? actor, CancellationToken ct) =>
        Purge(job, reasonMessage, actor, ct);

    Task<RaiseJobSignalResponse> IJobs.RaiseSignalAsync(
        JobLookup job,
        string name,
        JobPayload value,
        string? actor,
        CancellationToken ct
    ) => RaiseSignal(job, name, value, actor, ct);

    Task<ClearJobSignalResponse> IJobs.ClearSignalAsync(JobLookup job, string name, string? actor, CancellationToken ct) =>
        ClearSignal(job, name, actor, ct);

    Task<GetJobStatusResponse?> IJobs.GetStatusAsync(JobLookup job, CancellationToken ct) =>
        Task.FromResult(Detail(job) is { } found ? new GetJobStatusResponse(found.Status) : null);

    Task<TInput> IJobs.GetInputAsync<TInput>(JobLookup job, CancellationToken ct) => throw new NotSupportedException();

    Task<(bool Found, TResult? Value)> IJobs.TryGetResultAsync<TResult>(JobLookup job, CancellationToken ct)
        where TResult : default => throw new NotSupportedException();

    Task<T?> IJobs.GetVariableOrDefaultAsync<T>(JobLookup job, string name, CancellationToken ct)
        where T : default => throw new NotSupportedException();

    Task<TResult?> IJobs.GetStepResultOrDefaultAsync<TResult>(JobLookup job, string name, CancellationToken ct)
        where TResult : default => throw new NotSupportedException();

    Task<RaiseJobSignalResponse> IJobs.RaiseSignalAsync<T>(JobLookup job, string name, T value, string? actor, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobHandle> IJobs.EnqueueAsync<TInput>(TInput input, Action<JobEnqueueOptionsBuilder>? configure, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobHandle> IJobs.EnqueueAsync<TInput>(JobCall<TInput> job, Action<JobEnqueueOptionsBuilder>? configure, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobHandle<TResult>> IJobs.EnqueueAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    ) => throw new NotSupportedException();

    Task<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(
        DbTransaction transaction,
        JobCall<TInput> job,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    ) => throw new NotSupportedException();

    Task<EnqueueJobResponse> IJobs.EnqueueAsync<TInput, TResult>(
        DbTransaction transaction,
        JobCall<TInput, TResult> job,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    ) => throw new NotSupportedException();

    Task<JobOutcome> IJobs.WaitAsync(JobLookup job, TimeSpan? timeout, CancellationToken ct) => throw new NotSupportedException();

    Task<JobOutcome<TResult>> IJobs.WaitAsync<TResult>(JobLookup job, TimeSpan? timeout, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobOutcome> IJobs.CallAsync<TInput>(TInput input, JobExecutionOptions? options, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobOutcome> IJobs.CallAsync<TInput>(JobCall<TInput> job, JobExecutionOptions? options, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<JobOutcome<TResult>> IJobs.CallAsync<TInput, TResult>(
        JobCall<TInput, TResult> job,
        JobExecutionOptions? options,
        CancellationToken ct
    ) => throw new NotSupportedException();

    Task<EnqueueJobsResponse> IJobs.EnqueueJobsAsync(IReadOnlyList<EnqueueJobRequest> requests, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<EnqueueJobResponse> IJobs.EnqueueAsync(DbTransaction transaction, EnqueueJobRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    Task<EnqueueJobResponse> IJobs.EnqueueAsync<TInput>(
        DbTransaction transaction,
        TInput input,
        Action<JobEnqueueOptionsBuilder>? configure,
        CancellationToken ct
    ) => throw new NotSupportedException();

    Task<EnqueueJobsResponse> IJobs.EnqueueJobsAsync(
        DbTransaction transaction,
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken ct
    ) => throw new NotSupportedException();

    private static bool IsFound(JobLookup job) => job.JobRef == FoundJobRef || job.JobId == 42;

    // The same ref for the same id, so a document built from what the fakes answer reads the same every time.
    private static JobRef EnqueuedRef(long jobId) =>
        new(Guid.Parse("00000000-0000-0000-0000-" + jobId.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)));

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
            return Snapshot(42, "billing", "send-invoice", StoredInput, StoredResult);
        }

        // An enqueued ref resolves to its recorded request so the aggregate detail read can compose
        // it (id = 101 + insertion index); every other ref is unknown.
        if (Resolve(job) is { } enqueuedId && enqueuedId - 101 is >= 0 and var i && i < EnqueueRequests.Count)
        {
            var request = EnqueueRequests[(int)i];
            return Snapshot(enqueuedId, request.JobNamespace, request.JobName, _enqueuedInputs.GetValueOrDefault(enqueuedId), null);
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
        var jobRef = EnqueuedRef(id);
        _enqueuedRefs[jobRef.Value] = id;
        _enqueuedInputs[id] = request.Input.IsNone ? null : request.Input;
        if (request.DeduplicationKey is { Length: > 0 } fresh)
        {
            _deduplicated[fresh] = (id, jobRef);
        }

        return new EnqueueJobResponse(id, jobRef, EnqueueJobAction.Inserted);
    }

    private UpdateJobInputResponse Update(JobLookup job, JobPayload input)
    {
        InputUpdateCalls.Add(input);
        var (jobId, action, status, version) = ResultFor(job, applied: JobStatusCode.Ready);
        // The store refuses a job with no input, which has nothing to replace, and answers where the job stands.
        if (action == ControlAction.Applied && StoredInput is null)
        {
            return new UpdateJobInputResponse(jobId, ControlAction.Rejected, JobStatusCode.Ready, CurrentJobVersion);
        }

        if (action == ControlAction.Applied)
        {
            StoredInput = input;
        }

        return new UpdateJobInputResponse(jobId, action, status, version);
    }

    private Task<T> Control<T>(
        string verb,
        JobLookup job,
        string? reason,
        string? actor,
        long? expectedVersion,
        JobStatusCode applied,
        Func<long, ControlAction, JobStatusCode?, long?, T> respond
    )
    {
        ControlCalls.Add((verb, job.JobRef, reason, actor, expectedVersion));
        var (jobId, action, status, version) = ResultFor(job, expectedVersion, applied);
        return Task.FromResult(respond(jobId, action, status, version));
    }

    // An applied verb answers the status it leaves the found job in, and the next version where the verb carries one.
    private static (long JobId, ControlAction Action, JobStatusCode? Status, long? Version) ResultFor(
        JobLookup job,
        long? expectedVersion = null,
        JobStatusCode? applied = JobStatusCode.Ready
    ) =>
        job.JobRef == MissingJobRef ? (0, ControlAction.NotFound, null, null)
        : expectedVersion is { } expected && expected != CurrentJobVersion
            ? (42, ControlAction.VersionConflict, JobStatusCode.Paused, CurrentJobVersion)
        : job.JobRef == RejectedJobRef ? (43, ControlAction.Rejected, JobStatusCode.Succeeded, CurrentJobVersion)
        : (42, ControlAction.Applied, applied, CurrentJobVersion + 1);

    // The snapshot states the stored input and result by format and size, as the store's does.
    private static GetJobResponse Snapshot(long jobId, string jobNamespace, string jobName, JobPayload? input, JobPayload? result) =>
        new(
            JobId: jobId,
            JobRef: jobId == 42 ? FoundJobRef : EnqueuedRef(jobId),
            Kind: JobKindCode.Ordinary,
            LineageRootJobId: null,
            LineageRootJobRef: null,
            ParentJobId: null,
            ParentJobRef: null,
            DeduplicationKey: "ck-1",
            CorrelationKey: null,
            JobNamespace: jobNamespace,
            JobName: jobName,
            DefinitionId: 5,
            TenantId: null,
            TenantName: null,
            Status: JobStatusCode.Ready,
            Priority: JobPriorityCode.Normal,
            Lane: null,
            BlockedBehindJobId: null,
            BlockedBehindJobRef: null,
            ExecutionNumber: 0,
            FailureCount: 0,
            InputFormatId: input?.Format.Id ?? 0,
            InputByteLength: input is { IsNone: false } stored ? stored.Data.Length : null,
            ResultFormatId: result?.Format.Id ?? 0,
            ResultByteLength: result is { IsNone: false } produced ? produced.Data.Length : null,
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

    private static ExplainJobResponse Explanation() =>
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
            StepsHasMore: false,
            Reason: null,
            NextActions: [new ExplainJobAction("none", "no action needed - a worker will claim it")]
        );

    private static GetJobLineageMapResponse Lineage() =>
        new(
            Ancestors: [],
            AncestorsHasMore: false,
            Job: new GetJobLineageMapNode(
                JobId: 42,
                JobRef: FoundJobRef,
                JobNamespace: "billing",
                JobName: "send-invoice",
                Status: JobStatusCode.Ready,
                ParentJobId: null,
                ParentJobRef: null,
                LineageRootJobId: null,
                LineageRootJobRef: null,
                CreatedAtUtc: CreatedAtUtc,
                ModifiedAtUtc: CreatedAtUtc
            ),
            Steps: [],
            StepsHasMore: false,
            ActiveWait: null,
            Children:
            [
                new GetJobLineageMapChild(
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
