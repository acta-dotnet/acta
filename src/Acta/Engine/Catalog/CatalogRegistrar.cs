using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    /// <summary>
    /// Registers <paramref name="allDescriptors"/> into the namespace. <paramref name="registeredCatalogHash"/> is the hash
    /// the namespace's last schedule registration stored; a start that computes the same hash and plans no cursor or
    /// slot change skips that registration and its exclusive schedule lock.
    /// </summary>
    public async Task RegisterAsync(
        WorkerRegistration registration,
        int namespaceId,
        string? registeredCatalogHash,
        ImmutableArray<JobDescriptor> allDescriptors,
        CancellationToken ct
    )
    {
        var ns = registration.NamespaceName;
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
            effectiveById[c.DefinitionId] = c.Effective;
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

        List<string?> catalogParts =
        [
            registration.OwnerTeam,
            registration.Description,
            manifestGenerationUtc.ToString("O", CultureInfo.InvariantCulture),
        ];
        foreach (var c in catalog.Where(c => perNamespaceDefIds.ContainsKey(c.DefinitionName)).OrderBy(c => c.DefinitionId))
        {
            catalogParts.Add(c.DefinitionName);
            catalogParts.Add(c.DefinitionHash);
        }

        await ReconcileSchedulesAsync(ns, namespaceId, catalogParts, registeredCatalogHash, ct);
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
    /// The registration stores the hash of <paramref name="catalogParts"/> and the plan's declarations on the namespace.
    /// </summary>
    private async Task ReconcileSchedulesAsync(
        string ns,
        int namespaceId,
        List<string?> catalogParts,
        string? registeredCatalogHash,
        CancellationToken ct
    )
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

        var nowUtc = await _clock.GetUtcNowAsync(ct);
        var definitions = new List<DefinitionSchedules>(inScope.Count);

        foreach (var defId in inScope.Order())
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
                    EventRecording: descriptor.EventRecording,
                    SlotStatus: slotStatus,
                    SlotMinNextRunAtUtc: slotNextRun,
                    Schedules: slotSchedules
                )
            );
        }

        var catalogHash = CatalogHashOf(catalogParts, definitions);
        if (
            catalogHash == registeredCatalogHash
            && definitions.All(d =>
                IsRegistered(
                    d,
                    storedByDefinition.GetValueOrDefault(d.DefinitionId),
                    _context.DescriptorByDefinitionId[d.DefinitionId].Priority
                )
            )
        )
        {
            foreach (var d in definitions)
            {
                _context.RecurringSlotJobIds[storedByDefinition[d.DefinitionId].Values.First().SlotId] = 0;
            }
            return;
        }

        // A C#-allocated public ref per slot, consumed only when the slot job row is freshly
        // inserted; an existing slot keeps its stored ref (the upsert never overwrites job_ref).
        var slotRefs = new Guid[definitions.Count];
        for (var i = 0; i < slotRefs.Length; i++)
        {
            slotRefs[i] = JobRef.New().Value;
        }

        var slots = (
            await _schedules.RegisterSchedulesAsync(new RegisterSchedulesCommand(namespaceId, definitions, slotRefs, catalogHash), ct)
        ).Slots;
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
    /// The hash of <paramref name="catalogParts"/> and every declaration the plan writes: each slot's input and audit level
    /// and each schedule's declared fields, but no cursor or slot state, which <see cref="IsRegistered"/> compares instead.
    /// </summary>
    private static string CatalogHashOf(List<string?> catalogParts, List<DefinitionSchedules> plan)
    {
        var c = CultureInfo.InvariantCulture;
        catalogParts.Add(plan.Count.ToString(c));
        foreach (var d in plan)
        {
            catalogParts.AddRange([
                d.DefinitionId.ToString(c),
                d.InputFormatId.ToString(c),
                Convert.ToBase64String(d.Input.Span),
                ((byte)d.EventRecording).ToString(c),
                d.Schedules.Count.ToString(c),
            ]);
            foreach (var s in d.Schedules)
            {
                catalogParts.AddRange([
                    s.ScheduleName,
                    s.Expression,
                    s.TimeZoneId,
                    ((byte)s.ExpressionKind).ToString(c),
                    ((byte)s.MisfireStrategy).ToString(c),
                    s.Description,
                ]);
            }
        }

        return CatalogHash.Of([.. catalogParts]);
    }

    /// <summary>
    /// Whether <c>register_schedules</c> would write nothing for <paramref name="plan"/>: each planned schedule is stored
    /// with its planned cursor, no stored schedule is left to orphan, and the slot either rests where registration leaves
    /// it (in flight, waiting, finished, or held) or already stands at the planned status, next run, and priority.
    /// </summary>
    private static bool IsRegistered(DefinitionSchedules plan, Dictionary<string, StoredScheduleState>? stored, JobPriorityCode priority)
    {
        if (
            stored is null
            || stored.Count != plan.Schedules.Count
            || !plan.Schedules.All(s => stored.TryGetValue(s.ScheduleName, out var st) && st.NextOccurrenceAtUtc == s.NextOccurrenceAtUtc)
        )
        {
            return false;
        }

        var slot = stored.Values.First();
        return slot.SlotStatus
                is JobStatusCode.Executing
                    or JobStatusCode.Suspended
                    or JobStatusCode.Succeeded
                    or JobStatusCode.Failed
                    or JobStatusCode.Cancelled
            || ScheduleWalker.IsHeld(slot.SlotStatus, slot.SlotNextRunAtUtc)
            || (slot.SlotStatus == plan.SlotStatus && slot.SlotNextRunAtUtc == plan.SlotMinNextRunAtUtc && slot.SlotPriority == priority);
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

/// <summary>
/// Resolves the worker's manifest generation, the monotonic governor for definition promotion:
/// the explicit <see cref="JobsOptions.ManifestGenerationUtc"/> if set, otherwise the entry
/// assembly's file last-write-time, falling back to the running executable's last-write-time under
/// single-file or AOT publish (where the entry assembly reports no file location). Never process
/// start time. The result is normalized to UTC and truncated to millisecond precision so it survives
/// a DB write/read round trip on both providers.
/// </summary>
internal static class ManifestGenerationResolver
{
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "Assembly.Location is empty under single-file/AOT; that case falls back to the running executable below."
    )]
    public static DateTime Resolve(JobsOptions options, Assembly? entryAssembly, ILogger? log = null)
    {
        if (options.ManifestGenerationUtc is { } explicitGeneration)
        {
            return Normalize(DateTime.SpecifyKind(explicitGeneration, DateTimeKind.Utc));
        }

        // The entry assembly's file last-write-time, falling back to the running executable's publish
        // stamp when Assembly.Location is empty (single-file / AOT publish). Both are file timestamps
        // with the same monotonic-governor semantics, so a worker boots without an explicit generation.
        var resolved = Normalize(
            FileTimestamp(entryAssembly?.Location)
                ?? FileTimestamp(Environment.ProcessPath)
                ?? throw new InvalidOperationException(
                    "Acta could not resolve a manifest generation from the entry assembly or the running "
                        + "executable. Set JobsOptions.ManifestGenerationUtc explicitly."
                )
        );

        log?.LogWarning(
            "Acta manifest generation ({Detail}) was derived from the deployed file's last-write time; "
                + "container image copies and artifact restores can skew it and misorder definition promotion. "
                + "Set JobsOptions.ManifestGenerationUtc to a CI build stamp for deterministic ordering.",
            resolved.ToString("O", CultureInfo.InvariantCulture)
        );

        return resolved;

        static DateTime? FileTimestamp(string? path) =>
            !string.IsNullOrEmpty(path) && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    private static DateTime Normalize(DateTime utc) => new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
}

/// <summary>
/// Compares incoming descriptor contracts against the stored rows and reports eligible changes.
/// Pure: callers decide whether to warn or fail based on <see cref="PayloadContractDriftMode"/>.
/// </summary>
internal static class ContractDriftDetector
{
    public static IReadOnlyList<ContractDrift> Detect(
        DateTime incomingGenerationUtc,
        ImmutableArray<JobDescriptor> descriptors,
        IReadOnlyCollection<StoredDefinitionContract> stored
    )
    {
        if (descriptors.IsDefaultOrEmpty || stored.Count == 0)
        {
            return [];
        }

        var storedByName = new Dictionary<string, StoredDefinitionContract>(stored.Count, StringComparer.Ordinal);
        foreach (var s in stored)
        {
            storedByName[s.DefinitionName] = s;
        }

        var drifts = new List<ContractDrift>();
        foreach (var descriptor in descriptors)
        {
            if (!storedByName.TryGetValue(descriptor.JobName, out var existing))
            {
                continue;
            }

            if (incomingGenerationUtc < existing.ManifestGenerationAtUtc)
            {
                continue;
            }

            var incoming = JobDefinitionRegistration.ContractOf(descriptor);
            if (incoming != existing.Contract)
            {
                drifts.Add(new ContractDrift(descriptor.JobName, existing.Contract, incoming));
            }
        }

        return drifts;
    }
}

/// <summary>
/// Applies <see cref="PayloadContractDriftMode"/> to detected contract drift: fails worker startup
/// under Fail, logs a warning per drift under Warn. The warning is framed as live contract drift,
/// not corruption of already-enqueued rows.
/// </summary>
internal static class ContractDriftPolicy
{
    public static void Apply(PayloadContractDriftMode mode, IReadOnlyList<ContractDrift> drifts, string ns, ILogger log)
    {
        if (drifts.Count == 0)
        {
            return;
        }

        if (mode == PayloadContractDriftMode.Fail)
        {
            var names = string.Join(", ", drifts.Select(d => d.JobName));
            throw new PayloadContractDriftException(
                $"Payload contract drift in namespace '{ns}' for: {names}. "
                    + "Worker startup is blocked because PayloadContractDriftMode is Fail."
            );
        }

        foreach (var drift in drifts)
        {
            log.LogWarning(
                "Payload contract drift for job ({JobName}) in namespace ({Namespace}): ({Detail}). Enqueues made from now on use the new contract.",
                drift.JobName,
                ns,
                $"input type {drift.Stored.InputTypeName} -> {drift.Incoming.InputTypeName}, "
                    + $"input format {drift.Stored.InputFormatName} -> {drift.Incoming.InputFormatName}"
            );
        }
    }
}

/// <summary>
/// Which jobs of the generated <c>ActaJobs</c> framework manifest a worker namespace registers, and whether its worker
/// runs crash recovery. <see cref="JobsOptions.RegisterSystemJobs"/> brings the maintenance set; an outbox relay brings
/// <c>sys.outbox</c>, which is opt-in and registers only through <c>AddOutboxRelay</c>. Either one needs crash recovery
/// and <c>sys.alerts</c>, so a relay brings them even when automatic registration is off, and never forces the
/// unrelated <c>sys.retention</c>.
/// </summary>
internal static class SystemJobs
{
    public static IReadOnlySet<string> For(bool registerSystemJobs, bool hasRelay)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (registerSystemJobs || hasRelay)
        {
            names.Add("sys.alerts");
        }

        if (registerSystemJobs)
        {
            names.Add("sys.retention");
        }

        if (hasRelay)
        {
            names.Add("sys.outbox");
        }

        return names;
    }

    /// <summary>Whether the worker's heartbeat takes crash recovery passes, under the same rule as <c>sys.alerts</c>.</summary>
    public static bool RunsRecovery(bool registerSystemJobs, bool hasRelay) => registerSystemJobs || hasRelay;
}

/// <summary>
/// Environment gating for declared schedules. A <c>[JobSchedule]</c> whose <c>Environments</c> is empty
/// is a wildcard active in every environment; otherwise it registers only when the worker's current
/// environment (<see cref="JobsOptions.EnvironmentName"/>) matches one of its declared names,
/// case-insensitively, mirroring .NET host-environment comparison. A scoped schedule is inactive when
/// the current environment is unknown (null or empty), so an environment-scoped schedule never fires on
/// a host that has not declared which environment it is.
/// </summary>
internal static class ScheduleEnvironment
{
    public static bool IsActiveIn(ImmutableArray<string> environments, string? currentEnvironment) =>
        environments.IsDefaultOrEmpty || environments.Contains(currentEnvironment ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}
