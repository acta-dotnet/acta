using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Acta.Runtime.Hosting;
using Acta.Runtime.Modules.Execution.Api;
using Acta.Runtime.Modules.Execution.Definitions;
using Acta.Runtime.Modules.Execution.Schedules;
using Acta.Runtime.Services.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acta.Runtime.Modules.Execution.Workers;

/// <summary>
/// Owns the worker-runtime catalog upsert: namespace + definitions + <c>workers</c>, then the
/// startup schedule reconcile. Runs provider bootstraps first so the schema is in place before any
/// catalog write, then (worker mode only) populates the shared <see cref="WorkerContext"/> read on
/// the claim/dispatch hot path.
/// </summary>
/// <remarks>
/// Enqueue-only runtimes (a <c>j.Reference&lt;...&gt;(...)</c> host with no <c>j.Run&lt;...&gt;(...)</c>
/// worker) skip the worker-row write and leave the
/// context empty, but still run provider bootstraps so the schema is in place before any enqueue
/// resolves.
/// </remarks>
internal sealed class WorkerRuntimeInitializer(
    DefinitionsService definitions,
    IDefinitionStore definitionStore,
    IScheduleStore schedules,
    IWorkerStore workers,
    IActaClock clock,
    IServerClock serverClock,
    IJobPayloadSerializerRegistry serializers,
    IAlertRoutingCheck? alertRouting,
    IOptions<JobsOptions> options,
    WorkerRegistration? workerRegistration,
    WorkerContext context,
    ILogger? log = null
)
{
    private readonly DefinitionsService _definitions = definitions;
    private readonly IDefinitionStore _definitionStore = definitionStore;
    private readonly IScheduleStore _schedules = schedules;
    private readonly IWorkerStore _workers = workers;
    private readonly IActaClock _clock = clock;
    private readonly IServerClock _serverClock = serverClock;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly IAlertRoutingCheck? _alertRouting = alertRouting;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerRegistration? _workerRegistration = workerRegistration;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_workerRegistration is null)
        {
            return;
        }

        var ns = _workerRegistration.NamespaceName;

        // Fail fast on a skewed host clock before any catalog write or lease math. GetUtcNow needs only
        // the provider (a scalar clock read), not the Acta schema, so it is safe this early.
        await ValidateClockSkewAsync(ns, ct);

        // Namespace + worker register in one round trip (one transaction): the namespace is
        // hash-gate-upserted (no write on an unchanged restart, no key-range locks) and this process's
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
            var (registeredNamespaceId, registeredWorkerId) = await _workers.StartWorkerAsync(
                StartWorkerCommand.Create(
                    ns,
                    _workerRegistration.OwnerTeam,
                    _workerRegistration.Description,
                    Environment.MachineName,
                    deploymentVersion,
                    engineVersion,
                    RuntimeInformation.FrameworkDescription,
                    Environment.ProcessId,
                    _options.Value.MaxConcurrentExecutors
                ),
                ct
            );
            _context.NamespaceIds[ns] = registeredNamespaceId;
            _context.WorkerIdByNamespace[ns] = registeredWorkerId;
        }

        var namespaceId = _context.NamespaceIds[ns];

        // System jobs (e.g. sys.recovery) auto-register into every worker namespace ahead
        // of the user manifests (the runtime assembly's own generated RuntimeJobs), so each namespace carries
        // its maintenance catalog (unless JobsOptions.RegisterSystemJobs is off, e.g. external
        // maintenance). System jobs are identified by their reserved sys. names; every manifest's
        // descriptors land under this one worker's namespace.
        var perNamespaceDefIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var manifests = new List<JobDescriptorManifest>();

        // The automatic framework set registers when RegisterSystemJobs is on; an explicit relay adds
        // its sys.outbox/sys.recovery/sys.alerts subset even when that flag is off, without forcing
        // sys.retention. Both are subsets of the one generated RuntimeJobs manifest, filtered by name.
        var frameworkNames = new HashSet<string>(StringComparer.Ordinal);
        if (_options.Value.RegisterSystemJobs)
        {
            frameworkNames.UnionWith(FrameworkJobs.AutomaticNames);
        }
        if (_workerRegistration.Relay is not null)
        {
            frameworkNames.UnionWith(FrameworkJobs.RelayNames);
        }
        // sys.recovery is the only thing that marks dead workers and reclaims their in-flight jobs.
        // Without it a worker that dies takes its jobs with it - they stay Executing behind a lapsed
        // lease and are never re-run - and nothing surfaces that until a worker actually dies. Say it
        // at startup instead, because someone switching these off to trim overhead is not expecting
        // to have switched off crash recovery.
        if (!frameworkNames.Contains("sys.recovery"))
        {
            _log.LogWarning(
                "Namespace '{Namespace}': sys.recovery is not registered, so crashed workers are never marked dead and their "
                    + "in-flight jobs are never reclaimed. Those jobs stay Executing behind a lapsed lease permanently. Set "
                    + "JobsOptions.RegisterSystemJobs = true, or run an equivalent reclaim sweep yourself.",
                ns
            );
        }

        if (frameworkNames.Count > 0)
        {
            manifests.Add(
                new JobDescriptorManifest([.. RuntimeJobs.Descriptors.Descriptors.Where(d => frameworkNames.Contains(d.JobName))])
            );
        }
        manifests.AddRange(_workerRegistration.Manifests.Select(r => r.GetDescriptors()));

        // Register every manifest's descriptors in ONE batch: register_job_definitions retires the
        // namespace's definitions that are absent from its batch, so the batch must be the namespace's
        // complete set (system + all user manifests), not one manifest at a time, or each
        // call would retire the others' jobs. An empty combined set skips the call entirely (the
        // RegisterJobDefinitions.Run early-return), so an enqueue-only / manifest-less worker never sweeps.
        ImmutableArray<JobDescriptor> allDescriptors = [.. manifests.SelectMany(m => m.Descriptors)];
        ValidateHasDescriptors(ns, allDescriptors);
        ValidateUniqueJobNames(allDescriptors);
        ValidateScheduleTimeZones(allDescriptors);
        ValidateTenantRequirements(allDescriptors);

        // Resolve the monotonic generation, then gate contract drift before any catalog write: Fail
        // throws here (before register), Warn logs and continues. The SQL routine remains the
        // authoritative generation gate for the policy write itself.
        var manifestGenerationUtc = ManifestGenerationResolver.Resolve(_options.Value, Assembly.GetEntryAssembly(), _log);
        var storedContracts = await _definitionStore.GetDefinitionContractsAsync(namespaceId, ct);
        var contractDrifts = ContractDriftDetector.Detect(manifestGenerationUtc, allDescriptors, storedContracts);
        ContractDriftPolicy.Apply(_options.Value.PayloadContractDriftMode, contractDrifts, ns, _log);

        var defIds = await _definitions.RegisterAsync(namespaceId, manifestGenerationUtc, allDescriptors, storedContracts, ct);
        foreach (var (jobName, defId) in defIds)
        {
            perNamespaceDefIds[jobName] = defId;
        }

        // Index by definition id so RunOnceAsync dispatches without re-reading definitions. Each
        // descriptor is overlaid with the definition's effective (override-or-default) policy read from
        // the DB *_effective columns, so the execution hot path honors operator overrides. This is the
        // worker's live policy view; the reload tick re-overlays changed definitions without a restart.
        var catalog = await _definitionStore.GetDefinitionContractsAsync(namespaceId, ct);
        var effectiveById = new Dictionary<int, EffectiveJobPolicy>(catalog.Count);
        foreach (var c in catalog)
        {
            effectiveById[c.Id] = c.Effective;
        }
        foreach (var descriptor in allDescriptors)
        {
            if (defIds.TryGetValue(descriptor.JobName, out var defId))
            {
                _context.DescriptorByDefinitionId[defId] = effectiveById.TryGetValue(defId, out var eff)
                    ? EffectivePolicyOverlay.Apply(descriptor, eff)
                    : descriptor;
            }
        }
        _context.DefinitionIdsByNamespace[ns] = perNamespaceDefIds;

        var effectiveDescriptors = perNamespaceDefIds.Values.Select(id => _context.DescriptorByDefinitionId[id]).ToImmutableArray();
        _alertRouting?.ValidateRouting(ns, effectiveDescriptors);

        await ReconcileSchedulesAsync(namespaceId, ct);
    }

    // Worker-init clock-skew guard: measure the host-vs-DB clock offset (real GetUtcNow + the system
    // clock, deliberately NOT the schedule IActaClock that tests fake), then warn or throw per the
    // configured thresholds. AllowClockSkew downgrades the fail to a warning.
    private Task ValidateClockSkewAsync(string namespaceName, CancellationToken ct)
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

    // A manifest-less worker with framework jobs disabled would claim namespace jobs it can never
    // dispatch; the claimed rows would rot until lease recovery. Enqueue-only deployments don't call
    // Run() and never reach this initializer.
    internal static void ValidateHasDescriptors(string namespaceName, ImmutableArray<JobDescriptor> descriptors)
    {
        if (descriptors.IsEmpty)
        {
            throw new InvalidOperationException(
                $"Worker namespace '{namespaceName}' registers no job descriptors, so it would claim jobs it can never "
                    + "dispatch. Register at least one manifest on the worker builder, enable "
                    + "JobsOptions.RegisterSystemJobs, or use the enqueue-only registration instead of Run()."
            );
        }
    }

    // The generator rejects duplicate job names only within one generated manifest; a worker combines
    // the framework manifest plus every registered manifest, so the combined set must be validated
    // before any catalog write. A collision would otherwise register last-writer-wins and dispatch an
    // arbitrary one of the colliding handlers.
    internal static void ValidateUniqueJobNames(ImmutableArray<JobDescriptor> descriptors)
    {
        var duplicates = descriptors
            .GroupBy(d => d.JobName, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' ({string.Join(", ", g.Select(d => $"{d.HandlerType.FullName}.{d.MethodName}"))})")
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate job names across combined manifests: "
                    + string.Join("; ", duplicates)
                    + ". Job names must be unique within a worker namespace."
            );
        }
    }

    // Fail fast on an unresolvable schedule timezone before any catalog write. A bad identifier would
    // otherwise surface deep in the claim and fire path. This applies to cron schedules only; interval
    // schedules ignore zones by design. Operator overrides applied through SQL templates remain the
    // operator's responsibility.
    private static void ValidateScheduleTimeZones(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Schedules.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var schedule in descriptor.Schedules)
            {
                if (schedule.ExpressionKind != ScheduleExpressionKindCode.Cron || string.IsNullOrWhiteSpace(schedule.TimeZoneId))
                {
                    continue;
                }

                try
                {
                    _ = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    throw new InvalidOperationException(
                        $"Job '{descriptor.JobName}' schedule '{schedule.ScheduleName}' declares TimeZoneId "
                            + $"'{schedule.TimeZoneId}', which does not resolve on this host. Use an IANA id "
                            + "(e.g. \"Europe/Ljubljana\") or a Windows id known to the OS timezone database.",
                        ex
                    );
                }
            }
        }
    }

    // Fail fast on a tenant-required recurring definition: schedule slots are enqueued tenant-less by
    // the runtime, so every slot fire would trip the enqueue guard instead of surfacing at startup.
    private static void ValidateTenantRequirements(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.TenantRequirement == JobTenantRequirementCode.Required && !descriptor.Schedules.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' declares TenantRequirement = Required together with [JobSchedule]. "
                        + "Recurring slots are enqueued without a tenant, so a scheduled definition cannot require one."
                );
            }
        }
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

    // Definition-sourced recurring slots: reconcile each scheduled definition's slot + schedules
    // against persisted cursors at startup. The upsert set is the union of {descriptors that declare
    // >= 1 [JobSchedule]} and {definitions with persisted schedule state}, so removed schedules can
    // cancel an existing slot, while ordinary non-scheduled jobs are never touched.
    private async Task ReconcileSchedulesAsync(short namespaceId, CancellationToken ct)
    {
        var stored = await _schedules.GetScheduleStateAsync(namespaceId, ct);

        // definition id -> (schedule name -> stored cursor + operator lifecycle)
        var storedByDefinition = new Dictionary<int, Dictionary<string, StoredScheduleState>>();
        foreach (var s in stored)
        {
            if (!storedByDefinition.TryGetValue(s.DefinitionId, out var byName))
            {
                byName = new Dictionary<string, StoredScheduleState>(StringComparer.Ordinal);
                storedByDefinition[s.DefinitionId] = byName;
            }
            byName[s.ScheduleName] = s;
        }

        // Environment gating: a schedule registers here only when active in this worker's environment
        // (wildcard when it declares none). Excluded schedules are treated exactly as if undeclared, so
        // the union below still picks up a definition whose every schedule is now excluded *if* it has
        // persisted state, letting that prior slot cancel on this run.
        var env = _options.Value.EnvironmentName;

        var inScope = new HashSet<int>();
        foreach (var (defId, descriptor) in _context.DescriptorByDefinitionId)
        {
            if (
                !descriptor.Schedules.IsDefaultOrEmpty && descriptor.Schedules.Any(s => ScheduleEnvironment.IsActiveIn(s.Environments, env))
            )
            {
                inScope.Add(defId);
            }
        }
        foreach (var defId in storedByDefinition.Keys)
        {
            inScope.Add(defId);
        }

        if (inScope.Count == 0)
        {
            return;
        }

        var nowUtc = await _clock.GetUtcNowAsync(ct);
        var definitions = new List<DefinitionSchedules>(inScope.Count);

        foreach (var defId in inScope)
        {
            if (!_context.DescriptorByDefinitionId.TryGetValue(defId, out var descriptor))
            {
                // Persisted schedules for a definition no longer in this manifest: leave its slot
                // alone (definition retirement is a separate concern, not schedule reconciliation).
                continue;
            }

            var declared = descriptor.Schedules.IsDefaultOrEmpty
                ? (IReadOnlyList<ScheduleDescriptor>)[]
                : descriptor.Schedules.Where(s => ScheduleEnvironment.IsActiveIn(s.Environments, env)).ToList();
            var storedForDef = storedByDefinition.TryGetValue(defId, out var byName)
                ? byName
                : (IReadOnlyDictionary<string, StoredScheduleState>)new Dictionary<string, StoredScheduleState>();

            var (slotSchedules, slotMin) = ScheduleWalker.Reconcile(declared, storedForDef, nowUtc);

            var slotStatus =
                declared.Count == 0 ? JobStatusCode.Cancelled // descriptor dropped every [JobSchedule]
                : slotMin is null ? JobStatusCode.Paused // declared but every schedule is exhausted
                : JobStatusCode.Ready;

            var (inputFormatId, inputBytes) = SerializeSlotInput(descriptor);

            definitions.Add(
                new DefinitionSchedules(
                    NamespaceId: namespaceId,
                    DefinitionId: defId,
                    JobName: descriptor.JobName,
                    InputFormatId: inputFormatId,
                    Input: inputBytes,
                    AuditLevel: descriptor.AuditLevel,
                    SlotStatus: slotStatus,
                    SlotMinNextRunAtUtc: slotMin,
                    Schedules: slotSchedules
                )
            );
        }

        // A C#-allocated public ref per slot, consumed only when the slot job row is freshly
        // inserted; an existing slot keeps its stored ref (the upsert never overwrites job_ref).
        var slotRefs = new Guid[definitions.Count];
        for (var i = 0; i < slotRefs.Length; i++)
        {
            slotRefs[i] = JobRef.New().Value;
        }

        var slots = await _schedules.RegisterScheduledJobsAsync(new RegisterScheduledJobsCommand(definitions, slotRefs), ct);
        if (slots.Count != definitions.Count)
        {
            throw new InvalidOperationException(
                $"register_scheduled_jobs returned {slots.Count} slot ids for {definitions.Count} definitions. "
                    + "The routine must return exactly one slot id per definition."
            );
        }

        foreach (var slot in slots)
        {
            _context.RecurringSlotJobIds.Add(slot.SlotId);
        }
    }

    // Slot default input: fabricate new TIn() and serialize via the descriptor's emitted delegate.
    // No-payload / data-less inputs persist as format 0 with empty bytes.
    private (byte InputFormatId, ReadOnlyMemory<byte> Input) SerializeSlotInput(JobDescriptor descriptor)
    {
        if (descriptor.InputPayloadFormat.IsNone || descriptor.CreateDefaultInput is null || descriptor.SerializeInput is null)
        {
            return (0, ReadOnlyMemory<byte>.Empty);
        }

        var serializer = _serializers.Resolve(descriptor.InputPayloadFormat.Id);
        var payload = descriptor.SerializeInput(serializer, descriptor.CreateDefaultInput());
        return (payload.Format.Id, payload.Data);
    }
}
