using Acta.Configuration;
using Acta.Modules.Execution;
using Acta.Modules.Execution.ChildLatches;
using Acta.Modules.Execution.Signals;
using Acta.Modules.Execution.Workers;
using Acta.Relational.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Acta.Testing;

/// <summary>
/// Coarse outcome of one <see cref="IActaTestHost.RunOnceAsync"/> tick: the public mirror of the
/// runtime's internal run outcome, so integrators can assert on what a single drive did.
/// </summary>
public enum ActaRunOutcome : byte
{
    /// <summary>Nothing was claimable this tick (no Ready job, or a claim lost its row before start).</summary>
    NothingClaimed = 1,

    /// <summary>A job ran to terminal <c>Done</c>.</summary>
    Completed = 2,

    /// <summary>A job ran and threw; row terminal-<c>Failed</c>.</summary>
    Failed = 3,

    /// <summary>A job ran and re-armed itself (reschedule / durable sleep); back at <c>Ready</c>, budget-neutral.</summary>
    Rearmed = 4,
}

/// <summary>
/// Outcome of one test-driven recovery pass for a namespace.
/// </summary>
public sealed record ActaRecoveryOutcome(int DeadWorkersMarked, int ReclaimedJobs, int ReleasedChildLatches);

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
/// A running Acta runtime for tests: enqueue jobs, drive them deterministically with
/// <see cref="RunOnceAsync"/>, and assert through the <see cref="Jobs"/> read surface. Provider-agnostic:
/// the integrator wires their own <c>UseSqlServer</c> / <c>UsePostgres</c> in the start callback.
/// </summary>
public interface IActaTestHost : IAsyncDisposable
{
    /// <summary>The schema this host's runtime targets.</summary>
    string Schema { get; }

    /// <summary>The built service provider: escape hatch for resolving anything the runtime registered.</summary>
    IServiceProvider Services { get; }

    /// <summary>The public job control / enqueue surface.</summary>
    IJobs Jobs { get; }

    /// <summary>Claim + execute at most one job in <paramref name="jobNamespace"/>; returns what happened.</summary>
    Task<ActaRunOutcome> RunOnceAsync(string jobNamespace, CancellationToken ct = default);

    /// <summary>
    /// Claim + execute the specific job <paramref name="jobId"/> (by id), retrying a transient claim
    /// miss under parallel load. Single-worker hosts only; multi-worker hosts use the namespace overload.
    /// </summary>
    Task<ActaRunOutcome> RunOnceAsync(long jobId, CancellationToken ct = default);

    /// <summary>Convenience overload taking the enqueue result directly: <c>RunOnceAsync(enqueued, ct)</c>.</summary>
    Task<ActaRunOutcome> RunOnceAsync(JobEnqueueOutcome enqueued, CancellationToken ct = default);

    /// <summary>Run one framework recovery pass for <paramref name="jobNamespace"/>.</summary>
    Task<ActaRecoveryOutcome> RunRecoveryOnceAsync(string jobNamespace, CancellationToken ct = default);

    /// <summary>Move the job's next-run instant into the past without otherwise changing its state.</summary>
    Task ForceJobDueAsync(long jobId, CancellationToken ct = default);

    /// <summary>Move the next pending timer checkpoint, or the named pending timer, into the past.</summary>
    Task ForceTimerDueAsync(long jobId, string? name = null, CancellationToken ct = default);

    /// <summary>Move the next pending step retry, or the named pending step retry, into the past.</summary>
    Task ForceStepRetryDueAsync(long jobId, string? name = null, CancellationToken ct = default);

    /// <summary>Expire the current execution lease for a claimed job.</summary>
    Task ExpireExecutionLeaseAsync(long jobId, CancellationToken ct = default);
}

/// <summary>
/// Stands up an Acta runtime against an integrator's provider on a throwaway schema, for testing their
/// jobs with any test framework. The integrator wires the provider + their manifest in the callback;
/// the host migrates (when the provider's <c>ApplyMigrationsOnStartup</c> is set), registers the catalog,
/// and exposes <see cref="IJobs"/> + a deterministic <c>RunOnce</c> drive.
/// </summary>
public static class ActaTestHost
{
    /// <summary>
    /// Build and initialize a test host. <paramref name="configureJobs"/> receives the
    /// <see cref="IActaBuilder"/> and the target schema; the integrator calls e.g.
    /// <c>j.UsePostgres(opts =&gt; { opts.ConnectionString = …; opts.Schema = schema; opts.ApplyMigrationsOnStartup = true; }).AddManifest&lt;TManifest&gt;().Run&lt;TManifest&gt;(ns)</c>.
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

        // Same startup order as WorkerRuntimeHost: provider bootstraps (schema migrate, no-op unless
        // the provider's ApplyMigrationsOnStartup is set) before any worker's catalog upserts.
        foreach (var bootstrap in provider.GetServices<IProviderBootstrap>())
        {
            await bootstrap.RunAsync(ct);
        }

        var runtimes = provider.GetServices<WorkerRuntime>().ToArray();
        foreach (var runtime in runtimes)
        {
            await runtime.InitializeAsync(ct);
        }

        return new HostImpl(provider, runtimes, schema);
    }

    private sealed class HostImpl(ServiceProvider provider, WorkerRuntime[] runtimes, string schema) : IActaTestHost
    {
        public string Schema => schema;

        public IServiceProvider Services => provider;

        public IJobs Jobs => provider.GetRequiredService<IJobs>();

        private IDbSession Db => provider.GetRequiredService<IDbSession>();

        public async Task<ActaRunOutcome> RunOnceAsync(string jobNamespace, CancellationToken ct = default)
        {
            var runtime = ResolveRuntime(jobNamespace);
            var outcome = await runtime.RunOnceAsync(jobNamespace, ct);
            return (ActaRunOutcome)(byte)outcome;
        }

        public async Task<ActaRunOutcome> RunOnceAsync(long jobId, CancellationToken ct = default)
        {
            if (runtimes.Length != 1)
            {
                throw new InvalidOperationException(
                    runtimes.Length == 0
                        ? "RunOnceAsync requires a worker. Call Run<TManifest>(namespace) in the start callback."
                        : "RunOnceAsync(jobId) needs a single-worker host; use RunOnceAsync(namespace, ...) to disambiguate."
                );
            }

            var outcome = await runtimes[0].RunOnceAsync(jobId, ct);
            return (ActaRunOutcome)(byte)outcome;
        }

        public Task<ActaRunOutcome> RunOnceAsync(JobEnqueueOutcome enqueued, CancellationToken ct = default) =>
            RunOnceAsync(enqueued.JobId, ct);

        public async Task<ActaRecoveryOutcome> RunRecoveryOnceAsync(string jobNamespace, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobNamespace);
            var runtime = ResolveRuntime(jobNamespace);
            if (!runtime.RegisteredNamespaceIds.TryGetValue(jobNamespace, out var namespaceId))
            {
                throw new InvalidOperationException($"Namespace '{jobNamespace}' has no id yet. Call InitializeAsync before recovery.");
            }

            var options = provider.GetRequiredService<IOptions<JobsOptions>>().Value;
            var db = Db;
            var signals = provider.GetRequiredService<ISignalStore>();
            var deadWorkers = await provider
                .GetRequiredService<IWorkerStore>()
                .MarkDeadWorkersAsync((int)options.WorkerDeadAfter.TotalSeconds, ct);
            var reclaimed = await provider.GetRequiredService<IExecutionStore>().ReclaimStuckJobsAsync(namespaceId, ct);

            var released = 0;
            foreach (var (childId, parentId) in reclaimed.FailedChildren)
            {
                if (await RaiseChildLatch.Run(signals, childId, parentId, JobStatusCode.Failed, ct))
                {
                    released++;
                }
            }

            foreach (var latch in await provider.GetRequiredService<IExecutionStore>().GetStaleChildLatchesAsync(namespaceId, ct))
            {
                if (await RaiseChildLatch.Run(signals, latch.ChildJobId, latch.ParentJobId, latch.ChildStatus ?? JobStatusCode.Failed, ct))
                {
                    released++;
                }
            }

            var wakeup = provider.GetService<WorkerWakeupPublisher>();
            if (wakeup is not null && reclaimed.Reclaimed > 0)
            {
                await wakeup.WakeAsync(WorkerWakeupChannel.WorkerNamespace(jobNamespace), WorkerWakeupReason.WorkAvailable, ct);
            }
            if (wakeup is not null && released > 0)
            {
                await wakeup.WakeAsync(WorkerWakeupChannel.AllWorkerNamespaces, WorkerWakeupReason.WorkAvailable, ct);
            }

            return new ActaRecoveryOutcome(deadWorkers, reclaimed.Reclaimed, released);
        }

        public async Task ForceJobDueAsync(long jobId, CancellationToken ct = default)
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var affected = await Db.From<JobRuntime>()
                .Where(r => r.Id == jobId)
                .UpdateOnlyAsync(() => new JobRuntime { NextRunAtUtc = DateTime.UtcNow.AddMinutes(-1), ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException($"ForceJobDueAsync expected one runtime row for job {jobId}, found {affected}.");
            }
        }

        public async Task ForceTimerDueAsync(long jobId, string? name = null, CancellationToken ct = default)
        {
            var timer = await FindCheckpointAsync(jobId, JobCheckpointKindCode.Timer, JobCheckpointStateCode.Pending, name, ct);
            var due = DateTime.UtcNow.AddMinutes(-1);
            var affected = await Db.From<JobCheckpoint>()
                .Where(c => c.JobId == jobId && c.Kind == JobCheckpointKindCode.Timer && c.Name == timer.Name)
                .UpdateOnlyAsync(() => new JobCheckpoint { DueAtUtc = due, ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"ForceTimerDueAsync expected one timer row for job {jobId} name '{timer.Name}', found {affected}."
                );
            }

            await ForceJobDueAsync(jobId, ct);
        }

        public async Task ForceStepRetryDueAsync(long jobId, string? name = null, CancellationToken ct = default)
        {
            var steps = await Db.From<JobStep>().Where(s => s.JobId == jobId && s.State == JobStepStateCode.Pending).ToListAsync(ct);
            var candidates = steps.Where(s => s.NextRetryAtUtc is not null);
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates = candidates.Where(s => s.Name == name);
            }

            var step = candidates.OrderBy(s => s.NextRetryAtUtc).FirstOrDefault();
            if (step is null)
            {
                var target = string.IsNullOrWhiteSpace(name) ? "a pending step retry" : $"pending step retry '{name}'";
                throw new InvalidOperationException($"Job {jobId} has no {target} to force due.");
            }

            var due = DateTime.UtcNow.AddMinutes(-1);
            var affected = await Db.From<JobStep>()
                .Where(s => s.JobId == jobId && s.Name == step.Name)
                .UpdateOnlyAsync(() => new JobStep { NextRetryAtUtc = due, ModifiedAtUtc = DbFn.UtcNow }, ct);
            if (affected != 1)
            {
                throw new InvalidOperationException(
                    $"ForceStepRetryDueAsync expected one step row for job {jobId} name '{step.Name}', found {affected}."
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

            var affected = await Db.From<JobRuntime>()
                .Where(r => r.Id == jobId && r.LeasedByWorkerId != null)
                .UpdateOnlyAsync(
                    () => new JobRuntime { LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5), ModifiedAtUtc = DbFn.UtcNow },
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
            if (runtimes.Length == 0)
            {
                throw new InvalidOperationException(
                    "RunOnceAsync requires a worker. Call Run<TManifest>(namespace) in the start callback."
                );
            }

            return runtimes.Length == 1 ? runtimes[0] : runtimes.Single(r => r.RegisteredNamespaceIds.ContainsKey(jobNamespace));
        }

        private async Task<JobCheckpoint> FindCheckpointAsync(
            long jobId,
            JobCheckpointKindCode kind,
            JobCheckpointStateCode state,
            string? name,
            CancellationToken ct
        )
        {
            if (jobId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId), jobId, "Job id must be positive.");
            }

            var checkpoints = await Db.From<JobCheckpoint>()
                .Where(c => c.JobId == jobId && c.Kind == kind && c.State == state)
                .ToListAsync(ct);
            var candidates = checkpoints.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(name))
            {
                candidates = candidates.Where(c => c.Name == name);
            }

            var checkpoint = candidates.OrderBy(c => c.DueAtUtc ?? DateTime.MaxValue).FirstOrDefault();
            if (checkpoint is null)
            {
                var target = string.IsNullOrWhiteSpace(name) ? $"a pending {kind} checkpoint" : $"{kind} checkpoint '{name}'";
                throw new InvalidOperationException($"Job {jobId} has no {target} to force due.");
            }

            return checkpoint;
        }
    }
}
