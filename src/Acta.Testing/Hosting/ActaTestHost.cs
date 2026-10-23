using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Acta.Testing;

/// <summary>
/// Stands up an Acta runtime against an integrator's provider on a throwaway schema, for testing their
/// jobs with any test framework. The integrator wires the provider + their manifest in the callback;
/// the host migrates (when the provider's <c>ApplyMigrationsOnStartup</c> is set) or preflights the
/// existing migration history (when it is not), registers the catalog, and exposes <see cref="IJobs"/>
/// + a deterministic <c>RunOnce</c> drive.
/// </summary>
public static class ActaTestHost
{
    /// <summary>
    /// Build and initialize a test host. <paramref name="configureJobs"/> receives the
    /// <see cref="IActaBuilder"/> and the target schema; the integrator calls e.g.
    /// <c>j.UsePostgres(opts =&gt; { opts.ConnectionString = …; opts.Schema = schema; opts.ApplyMigrationsOnStartup = true; }).Run&lt;TManifest&gt;(ns)</c>
    /// (or <c>j.Run(ns, w =&gt; w.AddManifest&lt;TManifest&gt;())</c> for several manifests).
    /// <para>
    /// Set the provider's <c>ApplyMigrationsOnStartup</c> whenever the host owns its schema: nothing has
    /// provisioned a throwaway schema, so without it the bootstrap's migration-history preflight throws
    /// instead of migrating.
    /// </para>
    /// </summary>
    public static async Task<IActaTestHost> StartAsync(
        Action<IActaBuilder, string> configureJobs,
        ActaTestHostOptions? options = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(configureJobs);
        options ??= new ActaTestHostOptions();
        var schema = options.Schema ?? "acta_test_" + Guid.NewGuid().ToString("N")[..12];

        var services = new ServiceCollection();
        services.UseActa(j => configureJobs(j, schema));
        options.ConfigureServices?.Invoke(services);
        var provider = services.BuildServiceProvider(validateScopes: true);

        var runtimes = provider.GetServices<WorkerRuntime>().ToArray();
        await WorkerRuntimeStartup.RunAsync(provider.GetServices<IProviderBootstrap>(), runtimes, ct);

        // Lets the run-once helper stop retrying an empty claim as soon as the row can no longer be
        // claimed, instead of waiting out its budget to say so.
        var jobs = provider.GetRequiredService<IJobs>();
        foreach (var runtime in runtimes)
        {
            runtime.AttachClaimabilityProbe(jobs);
        }

        return new HostImpl(provider, runtimes, schema);
    }

    private sealed class HostImpl(ServiceProvider provider, WorkerRuntime[] runtimes, string schema) : IActaTestHost
    {
        public string Schema => schema;

        public IServiceProvider Services => provider;

        public IJobs Jobs => provider.GetRequiredService<IJobs>();

        public IActaOperations Operations => provider.GetRequiredService<IActaOperations>();

        private IDbSession Db => provider.GetRequiredService<IDbSession>();

        public async Task<ActaRunStatus> RunOnceAsync(string jobNamespace, CancellationToken ct = default)
        {
            var runtime = ResolveRuntime(jobNamespace);
            var outcome = await runtime.RunOnceAsync(jobNamespace, ct);
            return (ActaRunStatus)(byte)outcome;
        }

        public async Task<ActaRunStatus> RunOnceAsync(long jobId, CancellationToken ct = default)
        {
            if (runtimes.Length == 0)
            {
                throw new InvalidOperationException(
                    "RunOnceAsync requires a worker. Call Run<TManifest>(namespace) in the start callback."
                );
            }

            // Multi-worker hosts route by the job's own namespace, so Scenario drive helpers work
            // regardless of how many Run(...) namespaces the host registered.
            var runtime =
                runtimes.Length == 1
                    ? runtimes[0]
                    : ResolveRuntime(
                        (
                            await Jobs.GetAsync(JobLookup.ById(jobId), ct)
                            ?? throw new InvalidOperationException($"Job {jobId} does not exist; enqueue it before driving it.")
                        ).JobNamespace
                    );
            var outcome = await runtime.RunOnceAsync(jobId, ct);
            return (ActaRunStatus)(byte)outcome;
        }

        public Task<ActaRunStatus> RunOnceAsync(EnqueueJobResponse enqueued, CancellationToken ct = default) =>
            RunOnceAsync(enqueued.JobId, ct);

        public async Task<ActaRecoveryReport> RunRecoveryOnceAsync(string jobNamespace, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobNamespace);
            var runtime = ResolveRuntime(jobNamespace);
            if (!runtime.RegisteredNamespaceIds.TryGetValue(jobNamespace, out var namespaceId))
            {
                throw new InvalidOperationException($"Namespace '{jobNamespace}' has no id yet. Call InitializeAsync before recovery.");
            }

            var outcome = await provider.GetRequiredService<RecoveryPass>().RunAsync(namespaceId, jobNamespace, ct);
            return new ActaRecoveryReport(outcome.DeadWorkersMarked, outcome.ReclaimedJobs, outcome.ReleasedChildLatches);
        }

        public async Task ForceJobDueAsync(long jobId, CancellationToken ct = default)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var affected = await Db.From<JobStateEntity>()
                .Where(r => r.Id == jobId)
                .UpdateOnlyAsync(
                    () => new JobStateEntity { NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1), ModifiedAtUtc = DbFn.UtcNow },
                    ct
                );
            if (affected != 1)
            {
                throw new InvalidOperationException($"ForceJobDueAsync expected one jobs_state row for job {jobId}, found {affected}.");
            }
        }

        public async Task ForceTimerDueAsync(long jobId, string? name = null, CancellationToken ct = default)
        {
            var timer = await FindPendingTimerAsync(jobId, name, ct);
            var due = DateTime.UtcNow.AddMinutes(-1);
            var affected = await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Timer && c.CheckpointName == timer.CheckpointName)
                .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = due, ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"ForceTimerDueAsync expected one timer row for job {jobId} name '{timer.CheckpointName}', found {affected}."
                );
            }

            await ForceJobDueAsync(jobId, ct);
        }

        public async Task ForceStepRetryDueAsync(long jobId, string? name = null, CancellationToken ct = default)
        {
            var steps = await Db.From<StepEntity>().Where(s => s.JobId == jobId && s.Status == StepStatusCode.Pending).ToListAsync(ct);
            var candidates = steps.Where(s => s.NextRetryAtUtc is not null);
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates = candidates.Where(s => s.StepName == name);
            }

            var step = candidates.OrderBy(s => s.NextRetryAtUtc).FirstOrDefault();
            if (step is null)
            {
                var target = string.IsNullOrWhiteSpace(name) ? "a pending step retry" : $"pending step retry '{name}'";
                throw new InvalidOperationException($"Job {jobId} has no {target} to force due.");
            }

            var due = DateTime.UtcNow.AddMinutes(-1);
            var affected = await Db.From<StepEntity>()
                .Where(s => s.JobId == jobId && s.StepName == step.StepName)
                .UpdateOnlyAsync(() => new StepEntity { NextRetryAtUtc = due, ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"ForceStepRetryDueAsync expected one step row for job {jobId} name '{step.StepName}', found {affected}."
                );
            }

            await ForceJobDueAsync(jobId, ct);
        }

        public async Task ForceWaitTimeoutDueAsync(long jobId, string? name = null, CancellationToken ct = default)
        {
            var wait = await FindPendingWaitAsync(jobId, name, ct);
            var due = DateTime.UtcNow.AddMinutes(-1);
            var affected = await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == wait.Kind && c.CheckpointName == wait.CheckpointName)
                .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = due, ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"ForceWaitTimeoutDueAsync expected one wait row for job {jobId} name '{wait.CheckpointName}', found {affected}."
                );
            }

            // The deadline lives on the slot, but claimability lives on the jobs_state row: both must move
            // or the job stays parked past its own expiration.
            await ForceJobDueAsync(jobId, ct);
        }

        public async Task ForceGroupWaitTimeoutDueAsync(long jobId, CancellationToken ct = default)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var moved = await WaitTimeoutStaging.ForceGroupWaitDueAsync(Db, jobId, DateTime.UtcNow.AddMinutes(-1), ct);
            if (moved == 0)
            {
                throw new InvalidOperationException(
                    $"Job {jobId} stores no group wait deadline to force due; an unbounded group wait never times out."
                );
            }

            await ForceJobDueAsync(jobId, ct);
        }

        public async Task ExpireExecutionLeaseAsync(long jobId, CancellationToken ct = default)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var affected = await Db.From<JobStateEntity>()
                .Where(r => r.Id == jobId && r.LeasedByWorkerId != null)
                .UpdateOnlyAsync(
                    () => new JobStateEntity { LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), ModifiedAtUtc = DbFn.UtcNow },
                    ct
                );
            if (affected != 1)
            {
                throw new InvalidOperationException($"Job {jobId} does not currently have an execution lease to expire.");
            }
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();

        private WorkerRuntime ResolveRuntime(string jobNamespace)
        {
            return runtimes.Length == 0
                    ? throw new InvalidOperationException(
                        "RunOnceAsync requires a worker. Call Run<TManifest>(namespace) in the start callback."
                    )
                : runtimes.Length == 1 ? runtimes[0]
                : runtimes.Single(r => r.RegisteredNamespaceIds.ContainsKey(jobNamespace));
        }

        private async Task<CheckpointEntity> FindPendingTimerAsync(long jobId, string? name, CancellationToken ct)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var checkpoints = await Db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Timer && c.Status == CheckpointStatusCode.Pending)
                .ToListAsync(ct);
            var candidates = checkpoints.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates = candidates.Where(c => c.CheckpointName == name);
            }

            var checkpoint = candidates.OrderBy(c => c.DueAtUtc ?? DateTime.MaxValue).FirstOrDefault();
            if (checkpoint is null)
            {
                var target = string.IsNullOrWhiteSpace(name) ? "a pending Timer checkpoint" : $"Timer checkpoint '{name}'";
                throw new InvalidOperationException($"Job {jobId} has no {target} to force due.");
            }

            return checkpoint;
        }

        private async Task<CheckpointEntity> FindPendingWaitAsync(long jobId, string? name, CancellationToken ct)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var waits = await Db.From<CheckpointEntity>()
                .Where(c =>
                    c.JobId == jobId
                    && (c.Kind == CheckpointKindCode.Signal || c.Kind == CheckpointKindCode.ChildLatch)
                    && c.Status == CheckpointStatusCode.Pending
                )
                .ToListAsync(ct);
            var candidates = waits.Where(c => c.DueAtUtc is not null);
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates = candidates.Where(c => c.CheckpointName == name);
            }

            var wait = candidates.OrderBy(c => c.DueAtUtc).FirstOrDefault();
            return wait
                ?? throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(name)
                        ? $"Job {jobId} has no pending wait with an expiration to force due; an unbounded wait never times out."
                        : $"Job {jobId} has no pending wait '{name}' with an expiration to force due; an unbounded wait never times out."
                );
        }
    }
}

/// <summary>
/// Optional configuration for <see cref="ActaTestHost.StartAsync"/>.
/// </summary>
public sealed class ActaTestHostOptions
{
    /// <summary>
    /// The schema the host targets. Passed to the <c>configureJobs</c> callback so the provider call
    /// binds to it. Defaults to a unique throwaway name (<c>acta_test_{n}</c>).
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>Extra DI registrations (fakes, a deterministic <c>IActaClock</c>, …) applied after <c>UseActa</c>.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}

/// <summary>
/// Test-only drive helpers that claim a <em>specific</em> job by id and retry while the claim comes
/// back empty. The single-row claim uses READPAST, so under parallel load a sibling's concurrent
/// activity on the shared ready index can momentarily hide a freshly-committed row; production
/// tolerates this by ticking again (its batch loop never depends on one tick), so these reproduce that
/// for the deterministic single-shot test primitives. Retry up to a short budget, then return the last
/// result so a genuinely unclaimable job still surfaces <see cref="RunOnceStatus.NothingClaimed"/> /
/// an empty claim. The retry lives here (test code), never in the production claim path.
/// </summary>
/// <remarks>
/// An empty claim is transient only while the row is still claimable: a Ready row may yet be taken,
/// whether the empty answer was a skip or the row is not due; a Suspended row with a due instant is a
/// bounded wait the claim admits once that instant passes, and a scenario that ticks a parent toward a
/// child-wait deadline depends on that; a Suspended row with no due instant is an unbounded wait nothing
/// here can release, and every other status is owned, paused or finished. For those the answer will not
/// change however long this waits, and a fact expecting it used to wait out the whole budget to hear it,
/// which over the suite was more waiting than testing. A host attaches its facade per runtime and the
/// loop then stops as soon as the row is no longer claimable; a host that attaches nothing keeps the
/// budget. The claim-only helper keys on a store, not a runtime, and keeps the budget: it obtains a lease
/// that is expected to succeed, so it only ever pays on failure.
/// </remarks>
internal static class TestRunners
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    // The answer changes only when the claim itself succeeds, which the loop already sees, so the
    // status is read at most this often rather than on every empty claim.
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(500);

    private static readonly ConditionalWeakTable<WorkerRuntime, IJobs> s_facades = new();

    /// <summary>Lets the run-once loop stop retrying an empty claim once the row can no longer be claimed.</summary>
    internal static void AttachClaimabilityProbe(this WorkerRuntime runtime, IJobs jobs) => s_facades.AddOrUpdate(runtime, jobs);

    /// <summary>
    /// Convenience overload taking the enqueue result directly: <c>Runtime.RunOnceAsync(enqueued, ct)</c>.
    /// </summary>
    internal static Task<RunOnceStatus> RunOnceAsync(this WorkerRuntime runtime, EnqueueJobResponse enqueued, CancellationToken ct) =>
        runtime.RunOnceAsync(enqueued.JobId, ct);

    /// <summary>
    /// Claim and run the specific job <paramref name="jobId"/> in this runtime's (single) namespace,
    /// retrying the by-id claim while it is transiently skipped. The deterministic "run my job" drive.
    /// Retries only NothingClaimed: any settled outcome ends the loop, including Rearmed from an
    /// concurrency-key bounce, so a test that expects completion past a held key must tick again itself.
    /// </summary>
    internal static async Task<RunOnceStatus> RunOnceAsync(this WorkerRuntime runtime, long jobId, CancellationToken ct)
    {
        var jobNamespace = runtime.RegisteredNamespaceIds.Keys.Single();
        s_facades.TryGetValue(runtime, out var jobs);
        // The budget covers the retries, not the first drive: a drive that repeats a failing start or
        // completion paces its own retry and can outlast the budget on a loaded box, and the row it leaves
        // Ready still deserves the re-claim this loop exists for.
        Stopwatch? elapsed = null;
        var lastProbe = -ProbeEvery;
        while (true)
        {
            var outcome = await runtime.RunOnceAsync(jobNamespace, jobId, ct);
            elapsed ??= Stopwatch.StartNew();
            if (outcome != RunOnceStatus.NothingClaimed || elapsed.Elapsed > Budget)
            {
                return outcome;
            }

            if (jobs is not null && elapsed.Elapsed - lastProbe >= ProbeEvery)
            {
                lastProbe = elapsed.Elapsed;
                if (!await StillClaimableAsync(jobs, jobId, ct))
                {
                    return outcome;
                }
            }

            await Task.Delay(25, ct);
        }
    }

    private static async ValueTask<bool> StillClaimableAsync(IJobs jobs, long jobId, CancellationToken ct) =>
        await jobs.GetAsync(JobLookup.ById(jobId), ct) is { } job
        && (job.Status == JobStatusCode.Ready || (job.Status == JobStatusCode.Suspended && job.NextRunAtUtc is not null));

    /// <summary>
    /// Convenience overload taking the enqueue result directly.
    /// </summary>
    internal static Task<IReadOnlyList<ClaimedJob>> ClaimJobAsync(
        this ExecutionOperations execution,
        int namespaceId,
        int workerId,
        int leaseTtlSeconds,
        EnqueueJobResponse enqueued,
        CancellationToken ct
    ) => execution.ClaimJobAsync(namespaceId, workerId, leaseTtlSeconds, enqueued.JobId, ct);

    /// <summary>
    /// Claim the specific job <paramref name="jobId"/> without running it, retrying while the claim is
    /// transiently skipped. For setups that need a claimed-but-not-run lease (heartbeat / reclaim).
    /// </summary>
    internal static async Task<IReadOnlyList<ClaimedJob>> ClaimJobAsync(
        this ExecutionOperations execution,
        int namespaceId,
        int workerId,
        int leaseTtlSeconds,
        long jobId,
        CancellationToken ct
    )
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var claim = (
                await execution.ClaimJobAsync(new ClaimOptions(namespaceId, workerId, MaxBatch: 1), leaseTtlSeconds, jobId, ct)
            ).Jobs;
            if (claim.Count > 0 || elapsed.Elapsed > Budget)
            {
                return claim;
            }
            await Task.Delay(25, ct);
        }
    }
}

/// <summary>
/// The one implementation of "move a bounded group wait past its deadline", shared by
/// <see cref="IActaTestHost.ForceGroupWaitTimeoutDueAsync"/> and by the conformance specs that drive a
/// WorkerRuntime directly instead of a test host. Two copies of it could disagree about which rows a
/// staged expiry moves, and the failure mode is a test that reads as passing.
/// </summary>
internal static class WaitTimeoutStaging
{
    /// <summary>
    /// Rewrites every stored group deadline on <paramref name="jobId"/> to <paramref name="dueAtUtc"/>
    /// and moves every armed child latch to the same instant, which is what real elapsed time would do
    /// to both. Rewinding the latches alone would leave the deadline in the future and let a member
    /// armed after it start a fresh countdown. Returns how many group deadlines were moved, so the
    /// caller can decide whether finding none is an error. <paramref name="slotName"/> narrows the
    /// write to one group on a job holding several, which the public helper deliberately does not
    /// expose: an author has no way to know a slot's derived name.
    /// </summary>
    public static async Task<int> ForceGroupWaitDueAsync(
        IDbSession db,
        long jobId,
        DateTime dueAtUtc,
        CancellationToken ct,
        string? slotName = null
    )
    {
        var deadlines = await FindGroupDeadlinesAsync(db, jobId, slotName, ct);
        if (deadlines.Count == 0)
        {
            return 0;
        }

        // The bytes are written directly rather than through the payload serializer registry, because
        // this helper holds a db session and nothing else, and JSON for a long is its invariant decimal
        // digits either way. What the shortcut cannot see is a change of payload format on the sink, so
        // the format id is checked rather than assumed: a mismatch means the sink moved and this write
        // would quietly store bytes the runtime cannot read back.
        var value = Encoding.UTF8.GetBytes(dueAtUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        foreach (var name in deadlines)
        {
            await db.From<CheckpointEntity>()
                .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Variable && c.CheckpointName == name)
                .UpdateOnlyAsync(() => new CheckpointEntity { Value = value, ModifiedAtUtc = DbFn.UtcNow }, ct);
        }

        await db.From<CheckpointEntity>()
            .Where(c =>
                c.JobId == jobId
                && c.Kind == CheckpointKindCode.ChildLatch
                && c.Status == CheckpointStatusCode.Pending
                && c.DueAtUtc != null
            )
            .UpdateOnlyAsync(() => new CheckpointEntity { DueAtUtc = dueAtUtc, ModifiedAtUtc = DbFn.UtcNow }, ct);

        return deadlines.Count;
    }

    /// <summary>
    /// Filtered in memory rather than in the query: the slot name is a prefix match on a framework-owned
    /// key, and a job carries a handful of variables at most.
    /// </summary>
    private static async Task<IReadOnlyList<string>> FindGroupDeadlinesAsync(
        IDbSession db,
        long jobId,
        string? slotName,
        CancellationToken ct
    )
    {
        var variables = await db.From<CheckpointEntity>()
            .Where(c => c.JobId == jobId && c.Kind == CheckpointKindCode.Variable)
            .ToListAsync(ct);
        var deadlines = variables
            .Where(c => c.CheckpointName.StartsWith(JobContext.GroupDeadlinePrefix, StringComparison.Ordinal))
            .Where(c => slotName is null || c.CheckpointName == slotName)
            .ToList();

        foreach (var slot in deadlines)
        {
            if (slot.ValueFormatId != JobPayloadFormat.Json.Id)
            {
                throw new InvalidOperationException(
                    $"Group deadline slot '{slot.CheckpointName}' on job {jobId} carries payload format {slot.ValueFormatId}, not JSON. "
                        + "The staging helper writes the deadline as JSON ticks and has to move with the sink that stores it."
                );
            }
        }

        return [.. deadlines.Select(c => c.CheckpointName)];
    }
}
