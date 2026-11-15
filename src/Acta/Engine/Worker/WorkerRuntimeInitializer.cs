using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Owns the worker-runtime startup: the clock-skew check, the descriptor shape checks, the <c>workers</c> row, the
/// read of the namespace's deployed catalog into the shared <see cref="WorkerContext"/> read on the claim/dispatch hot
/// path, and on SQLite the release of this machine's ended workers; then the Stopped stamp on a clean shutdown. It writes no namespace, definition, or schedule: a deploy does
/// (<see cref="CatalogDeployer"/>), and a start refuses a namespace, a version, or a job that no deploy has written. An
/// enqueue-only runtime (a <c>j.Reference&lt;...&gt;(...)</c> host with no <c>j.Run&lt;...&gt;(...)</c> worker) skips
/// the worker-row write and leaves the context empty.
/// </summary>
internal sealed class WorkerRuntimeInitializer(
    DefinitionsOperations definitions,
    WorkersOperations workers,
    IClockOperations serverClock,
    AlertRoutingCheck? alertRouting,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    RecoveryPass recoveryPass,
    bool singleProcessDatabase,
    ActaMetrics? metrics,
    ILogger? log = null
)
{
    private readonly DefinitionsOperations _definitions = definitions;
    private readonly WorkersOperations _workers = workers;
    private readonly RecoveryPass _recoveryPass = recoveryPass;
    private readonly bool _singleProcessDatabase = singleProcessDatabase;
    private readonly ActaMetrics? _metrics = metrics;
    private readonly IClockOperations _serverClock = serverClock;
    private readonly AlertRoutingCheck? _alertRouting = alertRouting;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>
    /// Starts this worker in its namespace. With <paramref name="debug"/> set (<c>jobs debug</c>) the build need not
    /// have deployed there, and a job it declares may have no definition: it runs one stored job and that job's own.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct, bool debug = false)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;

        // Fail fast on a skewed host clock before the worker row or any lease math. GetUtcNow needs only
        // the provider (a scalar clock read), not the Acta schema, so it is safe this early.
        await ValidateClockSkewAsync(ns, ct);

        // Crash recovery is the only thing that marks dead workers and reclaims their in-flight jobs.
        // Without it a worker that dies takes its jobs with it - they stay Executing behind a lapsed
        // lease and are never re-run - and nothing surfaces that until a worker actually dies. Say it
        // at startup instead, because someone switching these off to trim overhead is not expecting
        // to have switched off crash recovery.
        _context.RunsRecovery = SystemJobs.RunsRecovery(_options.Value.RunSystemJobs, _workerRegistration.Relay is not null);
        if (!_context.RunsRecovery)
        {
            ActaLog.CrashRecoveryOff(_log, ns);
        }

        // The shape checks run before the worker row is written: a catalog that fails them must leave no Active
        // worker and no worker.started event behind for a process that will never heartbeat.
        var descriptors = CatalogRegistrar.CollectDescriptors(_workerRegistration, _options.Value);
        await StartWorkerAsync(ns, debug, ct);
        try
        {
            await LoadCatalogAsync(ns, _context.Worker.NamespaceId, descriptors, debug, ct);
        }
        catch
        {
            // The worker row is written. A start the catalog refuses leaves it Stopped, so the dead-worker sweep
            // does not report a crash for a process that never ran, and a second start writes a row of its own.
            try
            {
                await StopAsync(ct);
            }
            catch (Exception stopFailure)
                when (stopFailure is System.Data.Common.DbException or OperationCanceledException or TimeoutException)
            {
                // The refusal is what the caller needs; the row then waits for the sweep.
                ActaLog.WorkerStampFailed(_log, stopFailure, "stop", ns);
            }
            _context.WorkerId = null;
            throw;
        }

        if (_singleProcessDatabase && _context.RunsRecovery)
        {
            await ReleaseGoneWorkersAsync(ns, _context.Worker.NamespaceId, ct);
        }
    }

    /// <summary>
    /// On a database one process owns, SQLite, a worker row of this namespace on this machine whose process has ended
    /// is dead for good: the start marks it Dead, lapses its jobs' leases and holds, and runs one recovery pass, so
    /// those jobs run again now rather than a lease and a pass later. A server database keeps the lease wait, because
    /// replicas that share a host name keep process ids of their own. The release only brings the pass forward, so a
    /// failure is logged and leaves the jobs to the next pass.
    /// </summary>
    private async Task ReleaseGoneWorkersAsync(string ns, int namespaceId, CancellationToken ct)
    {
        var outcome = "completed";
        try
        {
            var gone = (await _workers.ListAsync(new ListWorkersQuery(JobNamespace: ns, PageSize: 100), ct))
                .Items.Where(w =>
                    w.Status is WorkerStatusCode.Active or WorkerStatusCode.Draining
                    && w.WorkerId != _context.WorkerId
                    && w.HostName == Environment.MachineName
                    && w.ProcessId is { } processId
                    && ProcessEnded(processId, w.StartedAtUtc)
                )
                .ToList();
            if (gone.Count == 0)
            {
                return;
            }

            await _workers.ReleaseGoneWorkersAsync([.. gone.Select(w => w.WorkerId)], ct);
            var released = string.Join(", ", gone.Select(w => $"{w.WorkerRef} (process {w.ProcessId})"));
            ActaLog.RecoveryPassChangedWork(_log, ns, $"released this machine's workers whose process has ended: {released}");
            await _recoveryPass.RunAsync(namespaceId, ns, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            outcome = "failed";
            ActaLog.RecoveryPassFailed(_log, ex, ns);
        }

        // A pass the start ran counts as the heartbeat's do; a start with nothing to release ran none.
        _metrics?.RecordRecoveryPass(ns, outcome);
    }

    /// <summary>
    /// Whether the process that wrote a worker row has ended: no process has its id, or the one that has it started
    /// more than a second after the row, a margin for a start time the system reports coarsely. A process this one may
    /// not inspect counts as running.
    /// </summary>
    private static bool ProcessEnded(int processId, DateTime rowStartedAtUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() > rowStartedAtUtc.AddSeconds(1);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the namespace's stored definitions into the context, indexing each descriptor this build declares by its
    /// definition id under the definition's effective policy, then checks alert routing and meter consistency on them.
    /// A declared job with no definition stops the start: the deploy ran with other options than this worker.
    /// </summary>
    private async Task LoadCatalogAsync(
        string ns,
        int namespaceId,
        ImmutableArray<JobDescriptor> descriptors,
        bool debug,
        CancellationToken ct
    )
    {
        var catalog = (await _definitions.ListDefinitionContractsAsync(namespaceId, ct)).Contracts;
        var storedByName = new Dictionary<string, StoredDefinitionContract>(catalog.Count, StringComparer.Ordinal);
        foreach (var c in catalog)
        {
            storedByName[c.DefinitionName] = c;
        }

        var undeployed = descriptors.Where(d => !storedByName.ContainsKey(d.JobName)).Select(d => d.JobName).ToList();
        if (undeployed.Count > 0 && !debug)
        {
            throw new InvalidOperationException(
                $"Worker namespace '{ns}' cannot start: this build declares jobs that are not deployed there ({string.Join(", ", undeployed)}). "
                    + "Run this app with the arguments 'jobs deploy' ('dotnet run -- jobs deploy' from its project), with the workers' "
                    + "configuration (the same RunSystemJobs and outbox relay)."
            );
        }

        var effectiveDescriptors = ImmutableArray.CreateBuilder<JobDescriptor>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            if (storedByName.TryGetValue(descriptor.JobName, out var stored))
            {
                var effective = EffectivePolicyOverlay.Apply(descriptor, stored.Effective);
                _context.DescriptorByDefinitionId[stored.DefinitionId] = effective;
                effectiveDescriptors.Add(effective);
            }
        }

        // Known before the first claim, so a retired definition's job is never started in the gap before a heartbeat.
        _context.ReplaceRetiredDefinitions(
            catalog
                .Where(c => c.Status == DefinitionStatusCode.Retired)
                .ToDictionary(c => c.DefinitionId, c => c.Effective.JobRetentionSeconds)
        );

        _alertRouting?.ValidateRouting(ns, effectiveDescriptors.ToImmutable());
        MeterConsistencyCheck.Check(ns, catalog, _log);
    }

    private async Task StartWorkerAsync(string ns, bool debug, CancellationToken ct)
    {
        // The in-process guard (worker already in context) makes a second InitializeAsync on the same instance append
        // no second worker row.
        if (_context.WorkerId is not null)
        {
            return;
        }

        // Host + deployment version stamp the append-only workers row, read once at startup, not on the hot path.
        var deploymentVersion = DeploymentVersionOf(_options.Value);
        var engineVersion = typeof(WorkerRuntimeInitializer)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "unknown";

        // The registration's public ref is allocated here, not by the database: the workers row is
        // append-only, so this process's ref is fixed for the lifetime of the registration. The
        // store writes address the row by its numeric id, so only the id is kept in context.
        var started = await _workers.StartWorkerAsync(
            StartWorkerCommand.Create(
                ns,
                Environment.MachineName,
                deploymentVersion,
                engineVersion,
                RuntimeInformation.FrameworkDescription,
                Environment.ProcessId,
                _options.Value.MaxConcurrentExecutors,
                WorkerRef.New().Value,
                requiresDeployment: !debug
            ),
            ct
        )
            ?? throw new InvalidOperationException(
                $"Worker namespace '{ns}' cannot start: nothing has been deployed there. Run this app with the arguments 'jobs deploy' "
                    + "('dotnet run -- jobs deploy' from its project), or 'jobs migrate', which also applies the schema, before the workers "
                    + "start, or set MigrateOnStartup in development."
            );
        if (started.WorkerId is not { } workerId)
        {
            throw new InvalidOperationException(
                $"Worker namespace '{ns}' cannot start: it is deployed at version '{started.CurrentDeploymentVersion}', and this "
                    + $"build ('{deploymentVersion}') has not deployed there. Run this app with the arguments 'jobs deploy' from this "
                    + "build first ('dotnet run -- jobs deploy' from its project)."
            );
        }

        _context.NamespaceId = started.NamespaceId;
        _context.WorkerId = workerId;

        // An earlier build during a rolling deploy, or a rollback: say whose definitions it runs.
        if (
            started.CurrentDeploymentVersion is { } current
            && !string.Equals(current, deploymentVersion, StringComparison.Ordinal)
            && _log.IsEnabled(LogLevel.Information)
        )
        {
            var detail = $"this build {deploymentVersion}, deployed {current}";
            ActaLog.WorkerStartedOnOtherVersion(_log, ns, detail);
        }
    }

    /// <summary>
    /// The version this process deploys and runs as: <see cref="JobsOptions.DeploymentVersion"/>, else the entry
    /// assembly's informational version, else <c>unknown</c>.
    /// </summary>
    internal static string DeploymentVersionOf(JobsOptions options) =>
        options.DeploymentVersion
        ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    /// <summary>
    /// Worker-init clock-skew guard: measure the host-vs-DB clock offset (real GetUtcNow + the
    /// system clock, deliberately NOT the schedule IActaClock that tests fake), then warn or throw
    /// per the configured thresholds. AllowClockSkew downgrades the fail to a warning.
    /// </summary>
    private Task<ClockSkewVerdict> ValidateClockSkewAsync(string namespaceName, CancellationToken ct)
    {
        var opts = _options.Value;
        var validator = new ClockSkewValidator(
            c => _serverClock.GetUtcNowAsync(c).AsTask(),
            TimeProvider.System,
            ClockSkewValidator.DefaultWarnThreshold,
            ClockSkewValidator.DefaultFailThreshold,
            opts.AllowClockSkew,
            _log
        );

        return validator.ValidateAsync(namespaceName, ct);
    }

    /// <summary>
    /// Clean-shutdown counterpart to <see cref="InitializeAsync"/>: marks this process's worker row
    /// Stopped and records a <c>worker.stopped</c> event. A no-op for enqueue-only runtimes and for a
    /// worker that never finished registering, in which case <c>mark_dead_workers</c> reaps it as Dead.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_context is { NamespaceId: { } namespaceId, WorkerId: { } workerId })
        {
            await _workers.StopWorkerAsync(namespaceId, workerId, ct);
        }
    }
}
