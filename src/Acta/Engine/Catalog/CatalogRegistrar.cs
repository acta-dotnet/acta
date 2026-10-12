using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Registers a worker namespace's catalog at startup: collects and validates the system and user descriptors, gates
/// contract drift, upserts the definitions, overlays each descriptor with its effective policy into the shared
/// <see cref="WorkerContext"/> read on the claim/dispatch hot path, and reconciles the definition-sourced recurring slots.
/// </summary>
internal sealed class CatalogRegistrar(
    DefinitionsOperations definitions,
    SchedulesOperations schedules,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    AlertRoutingCheck? alertRouting,
    IOptions<JobsOptions> options,
    WorkerContext context,
    ILogger log
)
{
    private readonly DefinitionsOperations _definitions = definitions;
    private readonly SchedulesOperations _schedules = schedules;
    private readonly IActaClock _clock = clock;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly AlertRoutingCheck? _alertRouting = alertRouting;
    private readonly IOptions<JobsOptions> _options = options;
    private readonly WorkerContext _context = context;
    private readonly ILogger _log = log;

    public async Task RegisterAsync(string ns, int namespaceId, ImmutableArray<JobDescriptor> allDescriptors, CancellationToken ct)
    {
        var perNamespaceDefIds = new Dictionary<string, int>(StringComparer.Ordinal);

        // Resolve the monotonic generation, then gate contract drift before any catalog write: Fail
        // throws here (before register), Warn logs and continues. The SQL routine remains the
        // authoritative generation gate for the policy write itself.
        var manifestGenerationUtc = ManifestGenerationResolver.Resolve(_options.Value, Assembly.GetEntryAssembly(), _log);
        var storedContracts = (await _definitions.GetDefinitionContractsAsync(namespaceId, ct)).Contracts;
        var contractDrifts = ContractDriftDetector.Detect(manifestGenerationUtc, allDescriptors, storedContracts);
        ContractDriftPolicy.Apply(_options.Value.PayloadContractDriftMode, contractDrifts, ns, _log);

        var defIds = (
            await _definitions.RegisterDefinitionsAsync(new(namespaceId, manifestGenerationUtc, allDescriptors, storedContracts), ct)
        ).DefinitionIds;
        foreach (var (jobName, defId) in defIds)
        {
            perNamespaceDefIds[jobName] = defId;
        }

        // Index by definition id so RunOnceAsync dispatches without re-reading definitions. Each
        // descriptor is overlaid with the definition's effective (override-or-default) policy read from
        // the DB *_effective columns, so the execution hot path honors operator overrides. This is the
        // worker's live policy view; the reload tick re-overlays changed definitions without a restart.
        var catalog = (await _definitions.GetDefinitionContractsAsync(namespaceId, ct)).Contracts;
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
        MeterConsistencyCheck.Check(ns, catalog, _log);

        await ReconcileSchedulesAsync(ns, namespaceId, ct);
    }

    public ImmutableArray<JobDescriptor> CollectDescriptors(WorkerRegistration registration, string ns)
    {
        // System jobs (e.g. sys.retention) auto-register into every worker namespace ahead
        // of the user manifests (the Acta assembly's own generated ActaJobs), so each namespace carries
        // its maintenance catalog (unless JobsOptions.RegisterSystemJobs is off, e.g. external
        // maintenance). System jobs are identified by their reserved sys. names; every manifest's
        // descriptors land under this one worker's namespace.
        var manifests = new List<JobDescriptorManifest>();

        var frameworkNames = SystemJobs.For(_options.Value.RegisterSystemJobs, registration.Relay is not null);
        // Crash recovery is the only thing that marks dead workers and reclaims their in-flight jobs.
        // Without it a worker that dies takes its jobs with it - they stay Executing behind a lapsed
        // lease and are never re-run - and nothing surfaces that until a worker actually dies. Say it
        // at startup instead, because someone switching these off to trim overhead is not expecting
        // to have switched off crash recovery.
        _context.RunsRecovery = SystemJobs.RunsRecovery(_options.Value.RegisterSystemJobs, registration.Relay is not null);
        if (!_context.RunsRecovery)
        {
            _log.LogWarning(
                "Namespace ({Namespace}): crash recovery is off, so crashed workers are never marked dead and their "
                    + "in-flight jobs are never reclaimed. Those jobs stay Executing behind a lapsed lease permanently. Set "
                    + "JobsOptions.RegisterSystemJobs = true, or run an equivalent reclaim sweep yourself.",
                ns
            );
        }

        if (frameworkNames.Count > 0)
        {
            manifests.Add(new JobDescriptorManifest([.. ActaJobs.Descriptors.Descriptors.Where(d => frameworkNames.Contains(d.JobName))]));
        }
        manifests.AddRange(registration.Manifests.Select(r => r.GetDescriptors()));

        // Register every manifest's descriptors in ONE batch (system + all user manifests): one round
        // trip, one set for the shape checks below, and every definition arrives with its complete
        // schedule set, which register_schedules needs because it orphans, on each slot it is
        // given, the definition-origin schedules that slot's batch omits. Registration touches nothing
        // the batch does not name, so a definition absent from it keeps running. An empty combined set
        // skips the call entirely (the RegisterDefinitions.Run early-return), so an enqueue-only /
        // manifest-less worker writes no catalog at all.
        // The shape checks run before the worker row is written: a catalog that fails them
        // must leave no Active worker and no worker.started event behind for a process that will
        // never heartbeat.
        ImmutableArray<JobDescriptor> allDescriptors = [.. manifests.SelectMany(m => m.Descriptors)];
        ValidateHasDescriptors(ns, allDescriptors);
        ValidateUniqueJobNames(allDescriptors);
        ValidateScheduleTimeZones(allDescriptors);
        ValidateTenantRequirements(allDescriptors);
        ValidateDeadlineRequirements(allDescriptors);
        ValidateRecurringResultCaps(allDescriptors);
        foreach (var descriptor in allDescriptors)
        {
            JobDefinitionRegistration.ValidateDescriptorShape(descriptor, ns);
        }
        return allDescriptors;
    }

    /// <summary>
    /// A manifest-less worker with framework jobs disabled would claim namespace jobs it can never
    /// dispatch; the claimed rows would rot until lease recovery. Enqueue-only deployments don't
    /// call Run() and never reach this registrar.
    /// </summary>
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

    /// <summary>
    /// The generator rejects duplicate job names only within one generated manifest; a worker
    /// combines the framework manifest plus every registered manifest, so the combined set must be
    /// validated before any catalog write. A collision would otherwise register last-writer-wins
    /// and dispatch an arbitrary one of the colliding handlers.
    /// </summary>
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

    /// <summary>
    /// Fail fast on an unresolvable schedule timezone before any catalog write. A bad identifier
    /// would otherwise surface deep in the claim and fire path. This applies to cron schedules
    /// only; interval schedules ignore zones by design. Operator overrides applied through SQL
    /// templates remain the operator's responsibility.
    /// </summary>
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

    /// <summary>
    /// Fail fast on a scheduled definition that declares a deadline: the deadline anchors to job
    /// creation and a recurring slot's row lives forever, so a Strict deadline would terminally
    /// cancel the slot the first time it is claimed past that anchor, and an Advisory one can never
    /// mean anything an occurrence could act on.
    /// </summary>
    internal static void ValidateDeadlineRequirements(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.DeadlineSeconds is not null && !descriptor.Schedules.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' declares Deadline together with [JobSchedule]. "
                        + "A deadline anchors to job creation, and a recurring slot is created once and lives "
                        + "forever, so the combination would cancel the slot instead of bounding an occurrence."
                );
            }
        }
    }

    /// <summary>
    /// Retention deletes terminal jobs and a live recurring slot is never terminal, so the cap is the only thing
    /// bounding a slot's result history, and zero means keep everything. Refused here, at startup and by name, rather
    /// than on the completion request, which is built after the handler has run and sits inside the write that is
    /// never abandoned.
    /// </summary>
    internal static void ValidateRecurringResultCaps(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (!descriptor.Schedules.IsDefaultOrEmpty && descriptor.RecurringResultCap < 1)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' declares [JobSchedule] with RecurringResultCap = {descriptor.RecurringResultCap}. "
                        + "A recurring slot keeps at least one result; a cap below one would keep every result it ever produces."
                );
            }
        }
    }

    /// <summary>
    /// Fail fast on a tenant-required recurring definition: schedule slots are enqueued tenant-less by the runtime, so
    /// every slot fire would trip the enqueue guard instead of surfacing at startup.
    /// </summary>
    private static void ValidateTenantRequirements(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.TenantRequirement == DefinitionTenantRequirementCode.Required && !descriptor.Schedules.IsDefaultOrEmpty)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' declares TenantRequirement = Required together with [JobSchedule]. "
                        + "Recurring slots are enqueued without a tenant, so a scheduled definition cannot require one."
                );
            }
        }
    }

    /// <summary>
    /// Definition-sourced recurring slots: reconcile each scheduled definition's slot + schedules
    /// against persisted cursors at startup. The upsert set is the union of {descriptors that
    /// declare >= 1 [JobSchedule]} and {definitions with persisted schedule state}, so removed
    /// schedules can pause an existing slot, while ordinary non-scheduled jobs are never touched.
    /// </summary>
    private async Task ReconcileSchedulesAsync(string ns, int namespaceId, CancellationToken ct)
    {
        var stored = (await _schedules.GetScheduleStateAsync(namespaceId, ct)).States;

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
        // persisted state, letting that prior slot pause on this run.
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
                ? (List<ScheduleDescriptor>)[]
                : descriptor.Schedules.Where(s => ScheduleEnvironment.IsActiveIn(s.Environments, env)).ToList();
            var storedForDef = storedByDefinition.TryGetValue(defId, out var byName)
                ? byName
                : (IReadOnlyDictionary<string, StoredScheduleState>)new Dictionary<string, StoredScheduleState>();

            var (slotSchedules, slotStatus, slotNextRun) = ScheduleWalker.Reconcile(declared, storedForDef, nowUtc);

            // Registration leaves a held job as it stands (ScheduleWalker.IsHeld); say so at every start.
            var slot = storedForDef.Values.FirstOrDefault();
            if (declared.Count > 0 && ScheduleWalker.IsHeld(slot?.SlotStatus, slot?.SlotNextRunAtUtc))
            {
                _log.LogWarning(
                    "Namespace ({Namespace}): recurring job {JobName} is paused by an operator and stays paused; it runs again "
                        + "only when an operator resumes it.",
                    ns,
                    descriptor.JobName
                );
            }

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
                    SlotMinNextRunAtUtc: slotNextRun,
                    Schedules: slotSchedules
                )
            );
        }

        // Every in-scope definition came from persisted state alone, so this manifest declares no slot
        // at all: a rolling deploy whose new generation carries no scheduled job into a namespace that
        // has some. There is nothing to reconcile, and the register verb reads its namespace from the
        // first row, so an empty batch has no namespace to send either.
        if (definitions.Count == 0)
        {
            return;
        }

        // A C#-allocated public ref per slot, consumed only when the slot job row is freshly
        // inserted; an existing slot keeps its stored ref (the upsert never overwrites job_ref).
        var slotRefs = new Guid[definitions.Count];
        for (var i = 0; i < slotRefs.Length; i++)
        {
            slotRefs[i] = JobRef.New().Value;
        }

        var slots = (await _schedules.RegisterSchedulesAsync(new RegisterSchedulesCommand(definitions, slotRefs), ct)).Slots;
        if (slots.Count != definitions.Count)
        {
            // A recurring slot's deduplication key is its job name, so an ordinary job that took that key
            // first is the one way a declared slot goes missing; registration leaves it untouched.
            var registered = slots.Select(s => s.DefinitionId).ToHashSet();
            var blocked = string.Join(", ", definitions.Where(d => !registered.Contains(d.DefinitionId)).Select(d => d.JobName));
            throw new InvalidOperationException(
                $"Namespace ({ns}): recurring job(s) {blocked} cannot register, because a job "
                    + "that is not the recurring slot already holds the deduplication key a slot uses, its job name. "
                    + "Finish and purge that job, or re-enqueue it under another key, then start the worker again."
            );
        }

        foreach (var slot in slots)
        {
            _context.RecurringSlotJobIds[slot.SlotId] = 0;
        }
    }

    /// <summary>
    /// Slot default input: fabricate new TIn() and serialize via the descriptor's emitted delegate.
    /// No-payload / data-less inputs persist as format 0 with empty bytes.
    /// </summary>
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
