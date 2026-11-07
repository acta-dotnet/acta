using System.Collections.Immutable;
using System.Data.Common;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Owns the worker-runtime startup: the clock-skew check, the namespace and <c>workers</c> upsert, the
/// <see cref="CatalogRegistrar"/> pass that registers the namespace's catalog, and the read of the stored catalog into
/// the shared <see cref="WorkerContext"/> read on the claim/dispatch hot path; then the Stopped stamp on a clean
/// shutdown. An enqueue-only runtime (a <c>j.Reference&lt;...&gt;(...)</c> host with no <c>j.Run&lt;...&gt;(...)</c>
/// worker) skips the worker-row write and leaves the context empty.
/// </summary>
internal sealed class WorkerRuntimeInitializer(
    CatalogRegistrar catalog,
    DefinitionsOperations definitions,
    WorkersOperations workers,
    IClockOperations serverClock,
    AlertRoutingCheck? alertRouting,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger? log = null
)
{
    private readonly CatalogRegistrar _catalog = catalog;
    private readonly DefinitionsOperations _definitions = definitions;
    private readonly WorkersOperations _workers = workers;
    private readonly IClockOperations _serverClock = serverClock;
    private readonly AlertRoutingCheck? _alertRouting = alertRouting;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log ?? NullLogger.Instance;

    /// <summary>The catalog hash the namespace held when this process's worker row was written.</summary>
    private string? _registeredCatalogHash;

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
                if (_log.IsEnabled(LogLevel.Information))
                {
                    var pass = $"pass {attempt}";
                    ActaLog.CatalogRegistrationRetrying(_log, ex, pass);
                }
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

        var allDescriptors = _catalog.CollectDescriptors(_workerRegistration, ns);
        await RegisterWorkerAsync(_workerRegistration, ns, ct);

        var namespaceId = _context.NamespaceIds[ns];
        await _catalog.RegisterAsync(_workerRegistration, namespaceId, _registeredCatalogHash, allDescriptors, ct);
        await LoadCatalogAsync(ns, namespaceId, allDescriptors, ct);
    }

    /// <summary>
    /// Reads the namespace's stored definitions into the context, indexing each descriptor this build declares by its
    /// definition id under the definition's effective policy, then checks alert routing and meter consistency on them.
    /// </summary>
    private async Task LoadCatalogAsync(string ns, int namespaceId, ImmutableArray<JobDescriptor> descriptors, CancellationToken ct)
    {
        var catalog = (await _definitions.ListDefinitionContractsAsync(namespaceId, ct)).Contracts;
        var storedByName = new Dictionary<string, StoredDefinitionContract>(catalog.Count, StringComparer.Ordinal);
        foreach (var c in catalog)
        {
            storedByName[c.DefinitionName] = c;
        }

        var definitionIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var effectiveDescriptors = ImmutableArray.CreateBuilder<JobDescriptor>(descriptors.Length);
        foreach (var descriptor in descriptors)
        {
            if (storedByName.TryGetValue(descriptor.JobName, out var stored))
            {
                var effective = EffectivePolicyOverlay.Apply(descriptor, stored.Effective);
                _context.DescriptorByDefinitionId[stored.DefinitionId] = effective;
                definitionIds[descriptor.JobName] = stored.DefinitionId;
                effectiveDescriptors.Add(effective);
            }
        }
        _context.DefinitionIdsByNamespace[ns] = definitionIds;

        _alertRouting?.ValidateRouting(ns, effectiveDescriptors.ToImmutable());
        MeterConsistencyCheck.Check(ns, catalog, _log);
    }

    private async Task RegisterWorkerAsync(WorkerRegistration registration, string ns, CancellationToken ct)
    {
        // Namespace + worker register in one round trip (one transaction): the namespace row is updated
        // only when its owner or description changed and inserted only when the name is absent (an unchanged
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
            var (registeredNamespaceId, registeredWorkerId, registeredCatalogHash) = await _workers.StartWorkerAsync(
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
            _registeredCatalogHash = registeredCatalogHash;
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
