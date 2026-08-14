using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Acta.Tests.AspNetCore;

/// <summary>
/// In-process dashboard host over a fake <see cref="IJobs"/> facade; no database, no network listener.
/// </summary>
internal static class TestDashboardHost
{
    /// <summary>Ref of the job the fakes know (internal id 42).</summary>
    public static readonly JobRef FoundJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000042"));

    /// <summary>Well-formed ref no fake job carries; controls report not found.</summary>
    public static readonly JobRef MissingJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000041"));

    /// <summary>Ref of the direct child the lineage fake reports under <see cref="FoundJobRef"/>.</summary>
    public static readonly JobRef ChildJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000007"));

    /// <summary>Ref whose control verbs report rejected.</summary>
    public static readonly JobRef RejectedJobRef = new(Guid.Parse("00000000-0000-0000-0000-000000000043"));

    public static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<ActaDashboardOptions>? configureDashboard = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        FakeJobs? jobs = null,
        Action<WebApplication>? configureApp = null
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var fake = jobs ?? new FakeJobs();
        builder.Services.AddSingleton<IJobs>(fake);
        builder.Services.AddSingleton<IActaOperations>(fake as IActaOperations ?? new FakeJobs());
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        configureApp?.Invoke(app);
        app.MapActa("/acta", configureDashboard);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    /// <summary>Same as <see cref="StartAsync"/>, but every request carries an authenticated
    /// <see cref="ClaimsPrincipal"/> named <paramref name="principalName"/> (set unconditionally by a
    /// middleware, no auth scheme involved) so control endpoints can read <c>http.User.Identity.Name</c>.</summary>
    public static Task<(WebApplication App, HttpClient Client)> StartAuthenticatedAsync(
        string principalName = "test-operator",
        Action<ActaDashboardOptions>? configureDashboard = null,
        FakeJobs? jobs = null
    ) =>
        StartAsync(
            configureDashboard,
            jobs: jobs,
            configureApp: app =>
                app.Use(
                    (ctx, next) =>
                    {
                        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, principalName)], "Test"));
                        return next(ctx);
                    }
                )
        );

    public sealed class FakeJobs : IJobs, IActaOperations, ILedger
    {
        public static readonly JobListItem Job = new(
            JobId: 42,
            JobRef: FoundJobRef,
            JobNamespace: "billing",
            JobName: "send-invoice",
            TenantId: null,
            TenantKey: null,
            ParentJobId: null,
            ParentJobRef: null,
            LineageRootId: null,
            LineageRootJobRef: null,
            DeduplicationKey: "ck-1",
            CorrelationKey: "trace-1",
            Status: JobStatusCode.Ready,
            Priority: JobPriorityCode.Normal,
            CreatedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
            ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
            NextRunAtUtc: null,
            ExecutionNumber: 0,
            FailureCount: 0
        );

        /// <summary>Recorded control calls. MissingJobRef reports not found, RejectedJobRef rejected, others applied.</summary>
        public List<(string Verb, JobRef JobRef, string? Reason, string? ActorKey)> ControlCalls { get; } = [];

        /// <summary>Recorded reschedule calls, which carry a next-run instant the other control verbs don't.</summary>
        public List<(JobRef JobRef, DateTime NextRunAtUtc, string? Reason, string? ActorKey)> RescheduleCalls { get; } = [];

        /// <summary>Recorded reprioritize calls, which carry a priority the other control verbs don't.</summary>
        public List<(JobRef JobRef, JobPriorityCode Priority, string? Reason, string? ActorKey)> ReprioritizeCalls { get; } = [];

        /// <summary>Recorded purge calls; unlike the other control verbs there is no reason to capture.</summary>
        public List<(JobRef JobRef, string? ActorKey)> PurgeCalls { get; } = [];

        /// <summary>Recorded signal raises with the delivered payload format and bytes (null when presence-only).</summary>
        public List<(JobRef JobRef, string Name, byte FormatId, byte[]? Value, string? ActorKey)> SignalCalls { get; } = [];

        /// <summary>Input the found job (id 42) carries. The amend endpoint reads it for the stored format
        /// and writes the amended payload back, and the detail read projects it. Null means a no-input job.</summary>
        public JobPayload? StoredInput { get; set; }

        /// <summary>Result the found job (id 42) has produced; null means it has produced none (detail result null).</summary>
        public JobPayload? StoredResult { get; set; }

        /// <summary>When true, ("billing", dedup "sys.outbox") resolves to the found job (id 42) named
        /// sys.outbox, so the overview outbox lens sees a relay slot whose result is <see cref="StoredResult"/>.</summary>
        public bool HasOutboxSlot { get; set; }

        /// <summary>Checkpoints the found job (id 42) carries; empty by default.</summary>
        public IReadOnlyList<JobCheckpointItem> StoredCheckpoints { get; set; } = [];

        /// <summary>Tenant id the found job (id 42) snapshot carries; null (no tenant) by default. Id 1
        /// carries the key "cust-001"; any other id models a purged tenant row (id without a key).</summary>
        public int? SnapshotTenantId { get; set; }

        /// <summary>When true the fake tenants list read throws, modelling an unavailable tenants surface.</summary>
        public bool TenantsListThrows { get; set; }

        /// <summary>Recorded UpdateJobInputAsync payloads (the format-resolved payload the endpoint built).</summary>
        public List<JobPayload> InputAmendCalls { get; } = [];

        /// <summary>Recorded enqueue requests; EnqueueAsync stores each input under a fresh ref for read-back.</summary>
        public List<JobEnqueueRequest> EnqueueRequests { get; } = [];

        private readonly Dictionary<Guid, long> _enqueuedRefs = [];
        private readonly Dictionary<long, JobPayload?> _enqueuedInputs = [];

        /// <summary>Recorded tenant register calls. A key of "bad key" reports an invalid opaque key.</summary>
        public List<(string TenantKey, string? DisplayName, string? Description)> TenantCalls { get; } = [];

        /// <summary>Recorded tenant suspend/resume calls. A key of "missing" reports not found.</summary>
        public List<(string TenantKey, string? ReasonMessage, string? ActorKey)> SuspendResumeTenantCalls { get; } = [];

        /// <summary>Recorded namespace admin calls (which verb, and with what args). A name of "missing"
        /// reports not found; "sys" or "bad key" throws.</summary>
        public List<(string Verb, string Name, string? ReasonMessage, string? ActorKey)> NamespaceAdminCalls { get; } = [];

        /// <summary>Recorded UpdateOverridesAsync calls. A schedule name of "missing" reports not found, "rejected"
        /// reports a stale-version rejection, and an expression of "bad-expr" throws ArgumentException.</summary>
        public List<(
            string ScheduleName,
            int ExpectedVersion,
            string? Expression,
            string? TimeZoneId,
            string? Note,
            string? ActorKey
        )> SetOverridesCalls { get; } = [];

        /// <summary>Recorded TriggerNowAsync calls. A schedule name of "missing" reports not found and
        /// "rejected" reports rejected (e.g. paused or in-flight).</summary>
        public List<(string ScheduleName, string? Note, string? ActorKey)> TriggerCalls { get; } = [];

        /// <summary>Recorded AcknowledgeAsync calls. An alertId of 0 reports not found.</summary>
        public List<(long AlertId, string? Note, string? ActorKey)> AcknowledgeCalls { get; } = [];

        /// <summary>Recorded ResolveAsync calls. An alertId of 0 reports not found.</summary>
        public List<(long AlertId, string? Note, string? ActorKey)> ResolveCalls { get; } = [];

        public Exception? ListJobsException { get; init; }

        /// <summary>When set, ListJobsAsync awaits <paramref name="ct"/> indefinitely instead of
        /// returning, so a client abort surfaces as a real <see cref="OperationCanceledException"/>
        /// carrying the request's <c>RequestAborted</c> token (mirrors a client disconnecting mid-read).</summary>
        public bool ListJobsAwaitCancellation { get; init; }

        /// <summary>When set, GetOverviewAsync signals <see cref="OverviewStarted"/> and then awaits
        /// request cancellation, allowing endpoint tests to cancel a database-like overview read.</summary>
        public bool OverviewAwaitCancellation { get; init; }

        public TaskCompletionSource OverviewStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Domain sub-facades over the canned read data; Tenants/Schedules record onto this fake's call lists.
        public ISchedules Schedules { get; }
        public IDefinitions Definitions { get; } = new FakeDefinitions();
        public IWorkers Workers { get; } = new FakeWorkers();
        public IAlerts Alerts { get; }
        public ITenants Tenants { get; }
        public INamespaces Namespaces { get; }
        public FakeTags TagsFake { get; } = new();

        public ITags Tags => TagsFake;
        public ISettings Settings => throw new NotSupportedException();

        public ILedger Ledger => this;

        public DbProvider Provider => DbProvider.Sqlite;

        public FakeJobs()
        {
            Tenants = new FakeTenants(TenantCalls, SuspendResumeTenantCalls, () => TenantsListThrows);
            Namespaces = new FakeNamespaces(NamespaceAdminCalls);
            Schedules = new FakeSchedules(SetOverridesCalls, TriggerCalls);
            Alerts = new FakeAlerts(AcknowledgeCalls, ResolveCalls);
        }

        // --- Job-domain + dashboard reads folded onto the root ---

        public ListJobsQuery? LastJobsQuery { get; private set; }

        public async ValueTask<PagedResult<JobListItem>> ListJobsAsync(ListJobsQuery query, CancellationToken ct = default)
        {
            LastJobsQuery = query;
            if (ListJobsAwaitCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            if (ListJobsException is not null)
            {
                throw ListJobsException;
            }

            return query.Cursor is not null
                ? throw new InvalidPageCursorException("Cursor operation does not match this query.")
                : new PagedResult<JobListItem>([Job], null, false, 50, null);
        }

        public ListEventsQuery? LastEventsQuery { get; private set; }

        public ValueTask<PagedResult<EventListItem>> ListEventsAsync(ListEventsQuery query, CancellationToken ct = default)
        {
            LastEventsQuery = query;
            return ValueTask.FromResult(new PagedResult<EventListItem>([], null, false, 50, query.IncludeTotal ? 0L : null));
        }

        public async ValueTask<OverviewSnapshot> GetOverviewAsync(OverviewQuery query, CancellationToken ct = default)
        {
            if (OverviewAwaitCancellation)
            {
                OverviewStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return
                query.JobNamespace is not null
                && query.JobNamespace.Any(ch => !char.IsAsciiLetterLower(ch) && !char.IsAsciiDigit(ch) && ch != '-')
                ? throw new InvalidQueryException("JobNamespace must be a kebab-case identifier.")
                : new OverviewSnapshot(3, 120, 1, 2, 4, 1, 1, 2, 5, 10, 3, 8, 90);
        }

        /// <summary>Only ("billing", "send-invoice") is known to this fake host; anything else is unregistered.</summary>
        public JobInputTemplate? GetInputTemplate(string jobNamespace, string jobName) =>
            jobNamespace == "billing" && jobName == "send-invoice"
                ? new JobInputTemplate("Billing.SendInvoice", JobPayloadFormat.Json, """{"invoiceId":0,"note":null}""")
                : null;

        // --- IJobs verbs ---

        private static JobControlResult ResultFor(JobLookup job) =>
            job.JobRef == MissingJobRef ? new JobControlResult(0, ControlAction.NotFound, null)
            : job.JobRef == RejectedJobRef ? new JobControlResult(43, ControlAction.Rejected, JobStatusCode.Succeeded)
            : new JobControlResult(42, ControlAction.Applied, JobStatusCode.Paused);

        private ValueTask<JobControlResult> Control(string verb, JobLookup job, string? reason, string? actorKey)
        {
            ControlCalls.Add((verb, job.JobRef, reason, actorKey));
            return ValueTask.FromResult(ResultFor(job));
        }

        private ValueTask<JobControlResult> Signal(JobLookup job, string name, JobPayload value, string? actorKey)
        {
            SignalCalls.Add((job.JobRef, name, value.Format.Id, value.IsNone ? null : value.Data.ToArray(), actorKey));
            return ValueTask.FromResult(ResultFor(job));
        }

        public ValueTask<JobDetail?> GetAsync(JobLookup job, CancellationToken ct = default)
        {
            if (job.DeduplicationKey == "sys.outbox")
            {
                return ValueTask.FromResult<JobDetail?>(
                    HasOutboxSlot && job.JobNamespace == "billing" ? Snapshot(42, "billing", "sys.outbox", null) : null
                );
            }

            if (job.JobRef == FoundJobRef || job.JobId == 42 || (job.JobNamespace == "billing" && job.DeduplicationKey == "ck-1"))
            {
                return ValueTask.FromResult<JobDetail?>(Snapshot(42, "billing", "send-invoice", SnapshotTenantId));
            }

            // An enqueued ref resolves to its recorded request so the aggregate detail read can compose
            // it (id = 101 + insertion index); every other ref is unknown.
            var id = Resolve(job);
            if (id is { } enqueuedId && enqueuedId - 101 is >= 0 and var i && i < EnqueueRequests.Count)
            {
                var request = EnqueueRequests[(int)i];
                return ValueTask.FromResult<JobDetail?>(Snapshot(enqueuedId, request.JobNamespace, request.JobName, null));
            }

            return ValueTask.FromResult<JobDetail?>(null);
        }

        private static JobDetail Snapshot(long jobId, string jobNamespace, string jobName, int? tenantId) =>
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
                ExecutionNumber: 0,
                FailureCount: 0,
                InputFormatId: 0,
                NextRunAtUtc: null,
                LeasedByWorkerId: null,
                LeaseExpiresAtUtc: null,
                ExclusiveKey: null,
                RetentionUntilUtc: null,
                CreatedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
            );

        public ValueTask<JobExplanation?> ExplainAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult<JobExplanation?>(
                job.JobRef == FoundJobRef || job.JobId == 42
                    ? new JobExplanation(
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
                        NextActions: [new JobExplainAction("none", "no action needed - a worker will claim it")]
                    )
                    : null
            );

        public ValueTask<JobLineageMap?> GetLineageMapAsync(
            JobLookup job,
            JobLineageMapOptions? options = null,
            CancellationToken ct = default
        ) =>
            ValueTask.FromResult<JobLineageMap?>(
                job.JobRef == FoundJobRef || job.JobId == 42
                    ? new JobLineageMap(
                        Ancestors: [],
                        Job: new JobLineageJob(
                            JobId: 42,
                            JobRef: FoundJobRef,
                            JobNamespace: "billing",
                            JobName: "send-invoice",
                            Status: JobStatusCode.Ready,
                            ParentJobId: null,
                            ParentJobRef: null,
                            LineageRootId: null,
                            LineageRootJobRef: null,
                            CreatedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                            ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc)
                        ),
                        Steps: [],
                        ActiveWait: null,
                        Children:
                        [
                            new JobLineageChild(
                                JobId: 7,
                                JobRef: ChildJobRef,
                                JobName: "render-pdf",
                                Status: JobStatusCode.Executing,
                                CreatedAtUtc: new DateTime(2026, 6, 12, 6, 1, 0, DateTimeKind.Utc),
                                ModifiedAtUtc: new DateTime(2026, 6, 12, 6, 1, 0, DateTimeKind.Utc)
                            ),
                        ],
                        ChildrenHasMore: false
                    )
                    : null
            );

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(JobEnqueueRequest request, CancellationToken ct = default)
        {
            EnqueueRequests.Add(request);

            // Second enqueue of a deduplication key matches the first, as the real store does: the
            // endpoint distinguishes created from matched, so the fake has to as well.
            if (request.DeduplicationKey is { Length: > 0 } key && _deduplicated.TryGetValue(key, out var existing))
            {
                return ValueTask.FromResult(new JobEnqueueOutcome(existing.Id, existing.Ref, JobEnqueueAction.Deduplicated));
            }

            var id = 100 + EnqueueRequests.Count;
            var jobRef = JobRef.New();
            _enqueuedRefs[jobRef.Value] = id;
            _enqueuedInputs[id] = request.Input.IsNone ? null : request.Input;
            if (request.DeduplicationKey is { Length: > 0 } fresh)
            {
                _deduplicated[fresh] = (id, jobRef);
            }
            return ValueTask.FromResult(new JobEnqueueOutcome(id, jobRef, JobEnqueueAction.Inserted));
        }

        private readonly Dictionary<string, (long Id, JobRef Ref)> _deduplicated = new(StringComparer.Ordinal);

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome> RunAndWaitAsync<TInput>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            JobContract<TInput> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
            JobContract<TInput, TResult> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            JobContract<NoInput> job,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobOutcome<TResult>> RunAndWaitAsync<TInput, TResult>(
            JobContract<TInput, TResult> job,
            TInput input,
            JobExecutionOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull
            where TResult : notnull => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
            IReadOnlyList<JobEnqueueRequest> requests,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            System.Data.Common.DbTransaction transaction,
            JobEnqueueRequest request,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput>(
            System.Data.Common.DbTransaction transaction,
            JobContract<TInput> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync<TInput, TResult>(
            System.Data.Common.DbTransaction transaction,
            JobContract<TInput, TResult> job,
            TInput input,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        )
            where TInput : notnull => throw new NotSupportedException();

        public ValueTask<JobEnqueueOutcome> EnqueueAsync(
            System.Data.Common.DbTransaction transaction,
            JobContract<NoInput> job,
            JobEnqueueOptions? options = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<JobEnqueueOutcome>> EnqueueBatchAsync(
            System.Data.Common.DbTransaction transaction,
            IReadOnlyList<JobEnqueueRequest> requests,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<long?> GetJobIdAsync(JobLookup job, CancellationToken ct = default) => ValueTask.FromResult(Resolve(job));

        private long? Resolve(JobLookup job) =>
            job.Kind == JobLookupKind.JobId ? job.JobId
            : job.JobRef == FoundJobRef ? 42
            : _enqueuedRefs.TryGetValue(job.JobRef.Value, out var id) ? id
            : null;

        public ValueTask<JobStatusCode?> GetStatusAsync(JobLookup job, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<JobPayload?> GetInputAsync(JobLookup job, CancellationToken ct = default)
        {
            var id = Resolve(job);
            return ValueTask.FromResult(
                id is null ? null
                : id == 42 ? StoredInput
                : _enqueuedInputs.GetValueOrDefault(id.Value)
            );
        }

        public ValueTask<JobPayload?> GetResultAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult(Resolve(job) == 42 ? StoredResult : null);

        public ValueTask<IReadOnlyList<JobCheckpointItem>> GetCheckpointsAsync(JobLookup job, CancellationToken ct = default) =>
            ValueTask.FromResult(Resolve(job) == 42 ? StoredCheckpoints : []);

        public ValueTask<TResult?> GetResultAsync<TResult>(JobLookup job, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<JobControlResult> CancelAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => Control("cancel", job, reasonMessage, actorKey);

        public ValueTask<JobControlResult> PauseAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => Control("pause", job, reasonMessage, actorKey);

        public ValueTask<JobControlResult> ResumeAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => Control("resume", job, reasonMessage, actorKey);

        public ValueTask<JobControlResult> RestartAsync(
            JobLookup job,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        ) => Control("restart", job, reasonMessage, actorKey);

        public ValueTask<JobControlResult> RescheduleAsync(
            JobLookup job,
            DateTime nextRunAtUtc,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        )
        {
            RescheduleCalls.Add((job.JobRef, nextRunAtUtc, reasonMessage, actorKey));
            return ValueTask.FromResult(ResultFor(job));
        }

        public ValueTask<JobControlResult> ReprioritizeAsync(
            JobLookup job,
            JobPriorityCode priority,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        )
        {
            ReprioritizeCalls.Add((job.JobRef, priority, reasonMessage, actorKey));
            return ValueTask.FromResult(ResultFor(job));
        }

        public ValueTask<JobControlResult> UpdateJobInputAsync(
            JobLookup job,
            JobPayload input,
            string? reasonMessage = null,
            string? actorKey = null,
            CancellationToken ct = default
        )
        {
            InputAmendCalls.Add(input);
            var result = ResultFor(job);
            if (result.Action == ControlAction.Applied)
            {
                StoredInput = input;
            }

            return ValueTask.FromResult(result);
        }

        public ValueTask<JobControlResult> PurgeAsync(JobLookup job, string? actorKey = null, CancellationToken ct = default)
        {
            PurgeCalls.Add((job.JobRef, actorKey));
            return ValueTask.FromResult(ResultFor(job));
        }

        public ValueTask<JobControlResult> RaiseSignalAsync(
            JobLookup job,
            string name,
            CancellationToken ct = default,
            string? actorKey = null
        ) => Signal(job, name, JobPayload.None, actorKey);

        public ValueTask<JobControlResult> RaiseSignalAsync<T>(
            JobLookup job,
            string name,
            T value,
            string? actorKey = null,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public ValueTask<JobControlResult> RaiseSignalAsync(
            JobLookup job,
            string name,
            JobPayload value,
            string? actorKey = null,
            CancellationToken ct = default
        ) => Signal(job, name, value, actorKey);

        // --- Fake domain sub-facades ---

        private sealed class FakeSchedules(
            List<(
                string ScheduleName,
                int ExpectedVersion,
                string? Expression,
                string? TimeZoneId,
                string? Note,
                string? ActorKey
            )> setOverridesCalls,
            List<(string ScheduleName, string? Note, string? ActorKey)> triggerCalls
        ) : ISchedules
        {
            public ValueTask<ScheduleControlResult> PauseAsync(
                ScheduleLookup schedule,
                DateTime? untilUtc = null,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => ValueTask.FromResult(new ScheduleControlResult(ControlAction.Applied, null, untilUtc, null, null));

            public ValueTask<ScheduleControlResult> ResumeAsync(
                ScheduleLookup schedule,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => ValueTask.FromResult(new ScheduleControlResult(ControlAction.Applied, null, null, null, null));

            public ValueTask<ScheduleControlResult> UpdateOverridesAsync(
                ScheduleLookup schedule,
                int expectedVersion,
                string? expression,
                string? timeZoneId,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                // Mirror the production guard: an invalid expression surfaces as ArgumentException.
                if (expression == "bad-expr")
                {
                    throw new ArgumentException("Expression 'bad-expr' is not a valid schedule expression.", nameof(expression));
                }

                if (schedule.ScheduleName == "missing")
                {
                    return ValueTask.FromResult(new ScheduleControlResult(ControlAction.NotFound, null, null, null, null));
                }

                if (schedule.ScheduleName == "rejected")
                {
                    return ValueTask.FromResult(
                        new ScheduleControlResult(ControlAction.Rejected, ScheduleStatusCode.Active, null, DateTime.UnixEpoch, 7)
                    );
                }

                setOverridesCalls.Add((schedule.ScheduleName, expectedVersion, expression, timeZoneId, reasonMessage, actorKey));
                return ValueTask.FromResult(
                    new ScheduleControlResult(
                        ControlAction.Applied,
                        ScheduleStatusCode.Active,
                        null,
                        DateTime.UnixEpoch,
                        expectedVersion + 1
                    )
                );
            }

            public ValueTask<ScheduleControlResult> TriggerNowAsync(
                ScheduleLookup schedule,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                if (schedule.ScheduleName == "missing")
                {
                    return ValueTask.FromResult(new ScheduleControlResult(ControlAction.NotFound, null, null, null, null));
                }

                if (schedule.ScheduleName == "rejected")
                {
                    return ValueTask.FromResult(
                        new ScheduleControlResult(ControlAction.Rejected, ScheduleStatusCode.Paused, null, null, null)
                    );
                }

                triggerCalls.Add((schedule.ScheduleName, reasonMessage, actorKey));
                return ValueTask.FromResult(
                    new ScheduleControlResult(ControlAction.Applied, ScheduleStatusCode.Active, null, DateTime.UnixEpoch, 1)
                );
            }

            public ValueTask<PagedResult<ScheduleListItem>> ListAsync(ListSchedulesQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(new PagedResult<ScheduleListItem>([], null, false, 50, null));

            /// <summary>A schedule name of "missing" reports not found; others return a canned preview.</summary>
            public ValueTask<SchedulePreview?> PreviewAsync(ScheduleLookup schedule, int count = 10, CancellationToken ct = default) =>
                ValueTask.FromResult<SchedulePreview?>(
                    schedule.ScheduleName == "missing"
                        ? null
                        : new SchedulePreview("0 9 * * *", "UTC", [new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc)])
                );
        }

        /// <summary>Target-agnostic tag fake: GetAsync returns <see cref="Current"/> (null reports an
        /// unknown target); mutations record their inputs and report <see cref="MutationResult"/>.</summary>
        public sealed class FakeTags : ITags
        {
            public TagSet? Current { get; set; } = new([new TagItem("env", "prod"), new TagItem("team")]);

            public TagMutationResult MutationResult { get; set; } = new(TagMutationAction.Applied);

            public List<TagInput> UpsertCalls { get; } = [];

            public List<string> RemoveCalls { get; } = [];

            public List<IReadOnlyList<TagInput>> ReplaceCalls { get; } = [];

            public ValueTask<TagSet?> GetAsync(TagTarget target, CancellationToken ct = default) => ValueTask.FromResult(Current);

            public ValueTask<TagMutationResult> ReplaceAsync(
                TagTarget target,
                IReadOnlyList<TagInput> tags,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                ReplaceCalls.Add(tags);
                return ValueTask.FromResult(MutationResult);
            }

            public ValueTask<TagMutationResult> UpsertAsync(
                TagTarget target,
                TagInput tag,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                UpsertCalls.Add(tag);
                return ValueTask.FromResult(MutationResult);
            }

            public ValueTask<TagMutationResult> RemoveAsync(
                TagTarget target,
                string name,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                RemoveCalls.Add(name);
                return ValueTask.FromResult(MutationResult);
            }
        }

        private sealed class FakeDefinitions : IDefinitions
        {
            public ValueTask<DefinitionControlResult> UpdateOverridesAsync(
                int definitionId,
                int expectedVersion,
                JobDefinitionPolicyOverrides overrides,
                string? actorKey = null,
                string? reasonMessage = null,
                CancellationToken ct = default
            )
            {
                // Mirror the production guard: an out-of-range override surfaces as ArgumentOutOfRangeException.
                return overrides.MaxAttempts is <= 0
                    ? throw new ArgumentOutOfRangeException(nameof(overrides.MaxAttempts), "MaxAttempts override must be at least 1.")
                    : ValueTask.FromResult(new DefinitionControlResult(ControlAction.Applied));
            }

            public ValueTask<JobDefinitionDetail?> GetAsync(int definitionId, CancellationToken ct = default) =>
                ValueTask.FromResult<JobDefinitionDetail?>(null);

            // The billing namespace carries one definition, id 5 - the same id the fake snapshot reports
            // as its DefinitionId, so the definition link on the job screen addresses a real row here.
            public ValueTask<PagedResult<JobDefinitionListItem>> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(
                    query.JobNamespace == "billing"
                        ? new PagedResult<JobDefinitionListItem>(
                            [
                                new JobDefinitionListItem(
                                    5,
                                    "billing",
                                    "send-invoice",
                                    JobDefinitionStatusCode.Active,
                                    "Billing.SendInvoice",
                                    null,
                                    null,
                                    JobPriorityCode.Normal,
                                    null,
                                    3,
                                    new DateTime(2026, 6, 12, 6, 0, 0, DateTimeKind.Utc),
                                    2
                                ),
                            ],
                            null,
                            false,
                            50,
                            null
                        )
                        : new PagedResult<JobDefinitionListItem>([], null, false, 50, null)
                );
        }

        private sealed class FakeWorkers : IWorkers
        {
            public ValueTask<WorkerDetail?> GetAsync(int workerId, CancellationToken ct = default) =>
                ValueTask.FromResult<WorkerDetail?>(
                    workerId == 42
                        ? new WorkerDetail(
                            42,
                            "billing",
                            WorkerStatusCode.Active,
                            "worker-host",
                            "deploy-42",
                            "engine-1",
                            ".NET 10.0.0",
                            4242,
                            8,
                            new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc),
                            new DateTime(2026, 6, 12, 7, 0, 0, DateTimeKind.Utc),
                            new DateTime(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc)
                        )
                        : null
                );

            public ValueTask<PagedResult<WorkerListItem>> ListAsync(ListWorkersQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(new PagedResult<WorkerListItem>([], null, false, 50, null));
        }

        /// <summary>An alertId of 0 reports not found; every other alertId applies.</summary>
        private sealed class FakeAlerts(
            List<(long AlertId, string? Note, string? ActorKey)> acknowledgeCalls,
            List<(long AlertId, string? Note, string? ActorKey)> resolveCalls
        ) : IAlerts
        {
            public static readonly DateTime AcknowledgedAt = new(2026, 6, 12, 7, 0, 0, DateTimeKind.Utc);
            public static readonly DateTime ResolvedAt = new(2026, 6, 12, 8, 0, 0, DateTimeKind.Utc);

            public ValueTask<AlertControlResult> AcknowledgeAsync(
                long alertId,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                if (alertId == 0)
                {
                    return ValueTask.FromResult(new AlertControlResult(alertId, ControlAction.NotFound, null, null));
                }

                acknowledgeCalls.Add((alertId, reasonMessage, actorKey));
                return ValueTask.FromResult(new AlertControlResult(alertId, ControlAction.Applied, AcknowledgedAt, null));
            }

            public ValueTask<AlertControlResult> ResolveAsync(
                long alertId,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                if (alertId == 0)
                {
                    return ValueTask.FromResult(new AlertControlResult(alertId, ControlAction.NotFound, null, null));
                }

                resolveCalls.Add((alertId, reasonMessage, actorKey));
                return ValueTask.FromResult(new AlertControlResult(alertId, ControlAction.Applied, null, ResolvedAt));
            }

            public ValueTask<AlertDetail?> GetAsync(long alertId, CancellationToken ct = default) =>
                ValueTask.FromResult<AlertDetail?>(null);

            public ValueTask<PagedResult<AlertListItem>> ListAsync(ListAlertsQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(new PagedResult<AlertListItem>([], null, false, 50, null));
        }

        private sealed class FakeTenants(
            List<(string TenantKey, string? DisplayName, string? Description)> tenantCalls,
            List<(string TenantKey, string? ReasonMessage, string? ActorKey)> suspendResumeCalls,
            Func<bool> listThrows
        ) : ITenants
        {
            public ValueTask<int> RegisterAsync(
                string tenantKey,
                string? displayName = null,
                string? description = null,
                CancellationToken ct = default
            )
            {
                // Mirror the production guard: an opaque-key validation failure surfaces as ArgumentException.
                if (tenantKey == "bad key")
                {
                    throw new ArgumentException("Tenant key must not contain whitespace.", nameof(tenantKey));
                }

                tenantCalls.Add((tenantKey, displayName, description));
                return ValueTask.FromResult(7);
            }

            public ValueTask<TenantDetail?> GetAsync(string tenantKey, CancellationToken ct = default) =>
                ValueTask.FromResult<TenantDetail?>(
                    tenantKey == "cust-001"
                        ? new TenantDetail(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, default, default, 0)
                        : null
                );

            public ValueTask<PagedResult<TenantListItem>> ListAsync(ListTenantsQuery query, CancellationToken ct = default) =>
                listThrows()
                    ? throw new InvalidOperationException("Tenants surface unavailable.")
                    : ValueTask.FromResult(
                        new PagedResult<TenantListItem>(
                            [new TenantListItem(1, "cust-001", "Acme", "Acme Corp", TenantStatusCode.Active, default, default, 0)],
                            null,
                            false,
                            50,
                            null
                        )
                    );

            public ValueTask<AdminControlResult> SuspendAsync(
                string tenantKey,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => Apply(tenantKey, reasonMessage, actorKey);

            public ValueTask<AdminControlResult> ResumeAsync(
                string tenantKey,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => Apply(tenantKey, reasonMessage, actorKey);

            public ValueTask<AdminControlResult> UpdateAsync(
                string tenantKey,
                int expectedVersion,
                string? displayName,
                string? description,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                if (tenantKey is "bad key" or "sys")
                {
                    throw new ArgumentException("Tenant key must not contain whitespace.", nameof(tenantKey));
                }

                if (tenantKey == "missing")
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.NotFound, null));
                }

                if (expectedVersion == 999)
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.VersionConflict, 5));
                }

                suspendResumeCalls.Add((tenantKey, reasonMessage, actorKey));
                return ValueTask.FromResult(new AdminControlResult(AdminControlAction.Applied, 2));
            }

            private ValueTask<AdminControlResult> Apply(string tenantKey, string? reasonMessage, string? actorKey)
            {
                if (tenantKey is "bad key" or "sys")
                {
                    throw new ArgumentException("Tenant key must not contain whitespace.", nameof(tenantKey));
                }

                if (tenantKey == "missing")
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.NotFound, null));
                }

                suspendResumeCalls.Add((tenantKey, reasonMessage, actorKey));
                return ValueTask.FromResult(new AdminControlResult(AdminControlAction.Applied, 2));
            }
        }

        private sealed class FakeNamespaces(List<(string Verb, string Name, string? ReasonMessage, string? ActorKey)> adminCalls)
            : INamespaces
        {
            public ValueTask<PagedResult<string>> ListNamesAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(new PagedResult<string>(["billing", "reports"], null, false, 50, null));

            public ValueTask<PagedResult<NamespaceListItem>> ListAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
                ValueTask.FromResult(
                    new PagedResult<NamespaceListItem>(
                        [
                            new NamespaceListItem(1, "sys", NamespaceStatusCode.Active, null, "system", 0),
                            new NamespaceListItem(2, "billing", NamespaceStatusCode.Active, "payments", "billing jobs", 3),
                        ],
                        null,
                        false,
                        50,
                        null
                    )
                );

            public ValueTask<AdminControlResult> SuspendAsync(
                string name,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => Apply("suspend", name, reasonMessage, actorKey);

            public ValueTask<AdminControlResult> ResumeAsync(
                string name,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            ) => Apply("resume", name, reasonMessage, actorKey);

            public ValueTask<AdminControlResult> UpdateAsync(
                string name,
                int expectedVersion,
                string? ownerTeam,
                string? description,
                string? reasonMessage = null,
                string? actorKey = null,
                CancellationToken ct = default
            )
            {
                if (name is "sys" or "bad key")
                {
                    throw new ArgumentException("The system namespace sys cannot be suspended or edited.", nameof(name));
                }

                if (name == "missing")
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.NotFound, null));
                }

                if (expectedVersion == 999)
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.VersionConflict, 5));
                }

                adminCalls.Add(("patch", name, reasonMessage, actorKey));
                return ValueTask.FromResult(new AdminControlResult(AdminControlAction.Applied, 2));
            }

            private ValueTask<AdminControlResult> Apply(string verb, string name, string? reasonMessage, string? actorKey)
            {
                if (name is "sys" or "bad key")
                {
                    throw new ArgumentException("The system namespace sys cannot be suspended or edited.", nameof(name));
                }

                if (name == "missing")
                {
                    return ValueTask.FromResult(new AdminControlResult(AdminControlAction.NotFound, null));
                }

                adminCalls.Add((verb, name, reasonMessage, actorKey));
                return ValueTask.FromResult(new AdminControlResult(AdminControlAction.Applied, 2));
            }
        }
    }
}
