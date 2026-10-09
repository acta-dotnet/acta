using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Owns the worker-runtime startup: the clock-skew check, the namespace and <c>workers</c> upsert, and the
/// <see cref="CatalogRegistrar"/> pass that registers the namespace's catalog, then the Stopped stamp on a clean
/// shutdown. In worker mode it populates the shared <see cref="WorkerContext"/> read on the claim/dispatch hot path. An
/// enqueue-only runtime (a <c>j.Reference&lt;...&gt;(...)</c> host with no <c>j.Run&lt;...&gt;(...)</c> worker) skips
/// the worker-row write and leaves the context empty.
/// </summary>
internal sealed class WorkerRuntimeInitializer(
    CatalogRegistrar catalog,
    WorkerLifecycle workers,
    IServerClock serverClock,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger? log = null
)
{
    private readonly CatalogRegistrar _catalog = catalog;
    private readonly WorkerLifecycle _workers = workers;
    private readonly IServerClock _serverClock = serverClock;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>
    /// Registers this worker's namespace, catalog, and recurring slots. Replicas of one build that start together
    /// register the same new rows at once, and the one that commits second sees its statement come back short or
    /// hit the unique key, since it could not see the first's uncommitted rows; a short pause and another pass
    /// reads them committed. A real conflict, such as an ordinary job holding a slot's key, fails every pass the
    /// same way.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await InitializeOnceAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException && attempt < CatalogAttempts)
            {
                _log.LogInformation(
                    ex,
                    "Registering the catalog failed on pass {Attempt}; another replica may be registering it. Retrying.",
                    attempt
                );
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), ct);
            }
        }
    }

    private const int CatalogAttempts = 5;

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;

        // Fail fast on a skewed host clock before any catalog write or lease math. GetUtcNow needs only
        // the provider (a scalar clock read), not the Acta schema, so it is safe this early.
        await ValidateClockSkewAsync(ns, ct);

        var allDescriptors = _catalog.CollectDescriptors(_workerRegistration, ns);
        await RegisterWorkerAsync(_workerRegistration, ns, ct);

        await _catalog.RegisterAsync(ns, _context.NamespaceIds[ns], allDescriptors, ct);
    }

    private async Task RegisterWorkerAsync(WorkerRegistration registration, string ns, CancellationToken ct)
    {
        // Namespace + worker register in one round trip (one transaction): the namespace row is updated
        // only when its catalog hash changed and inserted only when the name is absent (an unchanged
        // restart writes nothing, allocates no id, and takes no key-range locks) and this process's
        // worker row is appended. The worker row landing before definitions is harmless: definitions
        // only need the namespace id, which this call also returns. The in-process guard (worker
        // already in context) makes a second InitializeAsync on the SAME instance a no-op: it neither
        // re-upserts the namespace nor appends a second append-only worker row.
        if (!_context.WorkerIdByNamespace.TryGetValue(ns, out var _))
        {
            // Host + deployment version stamp the append-only workers row. DeploymentVersion is the
            // explicit option, else the entry assembly's InformationalVersion, else "unknown"; read
            // once at startup, not on the hot path.
            var deploymentVersion =
                _options.Value.DeploymentVersion
                ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown";
            var engineVersion =
                typeof(WorkerRuntimeInitializer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "unknown";
            // The registration's public ref is allocated here, not by the database: the workers row is
            // append-only, so this process's ref is fixed for the lifetime of the registration. The
            // store writes address the row by its numeric id, so only the id is kept in context.
            var workerRef = WorkerRef.New().Value;
            var (registeredNamespaceId, registeredWorkerId) = await _workers.StartWorkerAsync(
                StartWorkerCommand.Create(
                    ns,
                    registration.OwnerTeam,
                    registration.Description,
                    Environment.MachineName,
                    deploymentVersion,
                    engineVersion,
                    RuntimeInformation.FrameworkDescription,
                    Environment.ProcessId,
                    _options.Value.MaxConcurrentExecutors,
                    workerRef
                ),
                ct
            );
            _context.NamespaceIds[ns] = registeredNamespaceId;
            _context.WorkerIdByNamespace[ns] = registeredWorkerId;
        }
    }

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
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;
        if (_context.NamespaceIds.TryGetValue(ns, out var namespaceId) && _context.WorkerIdByNamespace.TryGetValue(ns, out var workerId))
        {
            await _workers.StopWorkerAsync(namespaceId, workerId, ct);
        }
    }
}
