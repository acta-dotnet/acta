using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Acta;

/// <summary>
/// Writes a worker namespace's catalog: collects and validates the system and user descriptors, gates contract drift,
/// upserts the definitions, and reconciles the definition-sourced recurring slots against each definition's effective
/// policy. It holds no worker state.
/// </summary>
internal sealed class CatalogRegistrar(
    DefinitionsOperations definitions,
    SchedulesOperations schedules,
    IActaClock clock,
    IJobPayloadSerializerRegistry serializers,
    IOptions<JobsOptions> options,
    CalendarsOperations calendars,
    ILogger log
)
{
    private readonly DefinitionsOperations _definitions = definitions;
    private readonly SchedulesOperations _schedules = schedules;
    private readonly IActaClock _clock = clock;
    private readonly IJobPayloadSerializerRegistry _serializers = serializers;
    private readonly IOptions<JobsOptions> _options = options;
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

        // Contract drift is gated before any catalog write: Fail throws here, Warn logs and continues.
        var storedContracts = (await _definitions.ListDefinitionContractsAsync(namespaceId, ct)).Contracts;
        ValidateScheduledDelivery(ns, allDescriptors, storedContracts);
        var contractDrifts = ContractDriftDetector.Detect(allDescriptors, storedContracts);
        ContractDriftPolicy.Apply(_options.Value.PayloadContractDriftMode, contractDrifts, ns, _log);
        await ValidateCalendarsAsync(ns, namespaceId, allDescriptors, storedContracts, ct);

        var defIds = (await _definitions.RegisterDefinitionsAsync(new(namespaceId, allDescriptors, storedContracts), ct)).DefinitionIds;

        // A slot is planned from its definition's effective (override-or-default) policy, read after the upsert from the
        // DB *_effective columns, so an operator override reaches the slot's priority.
        var catalog = (await _definitions.ListDefinitionContractsAsync(namespaceId, ct)).Contracts;
        var effectiveById = new Dictionary<int, EffectiveJobPolicy>(catalog.Count);
        foreach (var c in catalog)
        {
            effectiveById[c.DefinitionId] = c.Effective;
        }
        var descriptorById = new Dictionary<int, JobDescriptor>(defIds.Count);
        foreach (var descriptor in allDescriptors)
        {
            if (defIds.TryGetValue(descriptor.JobName, out var defId))
            {
                descriptorById[defId] = effectiveById.TryGetValue(defId, out var eff)
                    ? EffectivePolicyOverlay.Apply(descriptor, eff)
                    : descriptor;
            }
        }

        List<string?> catalogParts = [registration.OwnerTeam, registration.Description];
        foreach (var c in catalog.Where(c => defIds.ContainsKey(c.DefinitionName)).OrderBy(c => c.DefinitionId))
        {
            catalogParts.Add(c.DefinitionName);
            catalogParts.Add(c.DefinitionHash);
        }

        var retired = catalog.Where(c => c.Status == DefinitionStatusCode.Retired).Select(c => c.DefinitionId).ToHashSet();
        await ReconcileSchedulesAsync(ns, namespaceId, descriptorById, catalogParts, registeredCatalogHash, retired, ct);
    }

    public ImmutableArray<JobDescriptor> CollectDescriptors(WorkerRegistration registration, string ns)
    {
        // System jobs (e.g. sys.retention) auto-register into every worker namespace ahead
        // of the user manifests (the Acta assembly's own generated ActaJobs), so each namespace carries
        // its maintenance catalog (unless JobsOptions.RunSystemJobs is off, e.g. external
        // maintenance). System jobs are identified by their reserved sys. names; every manifest's
        // descriptors land under this one worker's namespace.
        var manifests = new List<JobDescriptorManifest>();

        var frameworkNames = SystemJobs.For(_options.Value.RunSystemJobs, registration.Relay is not null);
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
        ValidateScheduleBounds(allDescriptors);
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
                    + "JobsOptions.RunSystemJobs, or use the enqueue-only registration instead of Run()."
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
    /// Fail fast on a schedule bound that is not a UTC instant, or a start not earlier than the end. The generator
    /// reports the same rule as ACTA0129 for a generated manifest; this check is for a descriptor built by hand.
    /// </summary>
    internal static void ValidateScheduleBounds(ImmutableArray<JobDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Schedules.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var schedule in descriptor.Schedules)
            {
                RequireUtc(descriptor, schedule, nameof(ScheduleDescriptor.StartAtUtc), schedule.StartAtUtc);
                RequireUtc(descriptor, schedule, nameof(ScheduleDescriptor.EndBeforeUtc), schedule.EndBeforeUtc);
                if (schedule.StartAtUtc >= schedule.EndBeforeUtc)
                {
                    throw new InvalidOperationException(
                        $"Job '{descriptor.JobName}' schedule '{schedule.ScheduleName}' declares StartAtUtc "
                            + $"{schedule.StartAtUtc:O} and EndBeforeUtc {schedule.EndBeforeUtc:O}; occurrences fall in "
                            + "[start, end), so the start is earlier than the end."
                    );
                }
            }
        }

        static void RequireUtc(JobDescriptor descriptor, ScheduleDescriptor schedule, string member, DateTime? bound)
        {
            if (bound is { Kind: not DateTimeKind.Utc } value)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' schedule '{schedule.ScheduleName}' declares {member} of kind "
                        + $"{value.Kind}; a bound is a UTC instant."
                );
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
                    $"Job '{descriptor.JobName}' declares Deadline together with [Schedule]. "
                        + "A deadline anchors to job creation, and a recurring slot is created once and lives "
                        + "forever, so the combination would cancel the slot instead of bounding an occurrence."
                );
            }
        }
    }

    /// <summary>
    /// Refuses a definition whose declared delivery differs from the stored one, before any catalog write: a permanent
    /// slot and per-occurrence roots are different rows with different owners, so a mode never changes in place.
    /// </summary>
    internal static void ValidateScheduledDelivery(
        string namespaceName,
        ImmutableArray<JobDescriptor> descriptors,
        IReadOnlyCollection<StoredDefinitionContract> stored
    )
    {
        var storedByName = stored.ToDictionary(s => s.DefinitionName, s => s.Effective.ScheduledDelivery, StringComparer.Ordinal);
        foreach (var descriptor in descriptors)
        {
            if (storedByName.TryGetValue(descriptor.JobName, out var registered) && registered != descriptor.ScheduledDelivery)
            {
                throw new InvalidOperationException(
                    $"Job '{descriptor.JobName}' declares ScheduledDelivery = {descriptor.ScheduledDelivery}, but namespace "
                        + $"'{namespaceName}' registered it as {registered}. A definition's delivery is immutable once registered: "
                        + $"register the {descriptor.ScheduledDelivery} mode under a new job name."
                );
            }
        }
    }

    /// <summary>
    /// Refuses the start before any catalog write when a schedule this worker registers names a calendar that is not
    /// ready, listing every such schedule with what its calendar lacks: no published version, a version of the other
    /// role, or a version this build cannot read. A calendar is published by a deploy step before the workers that name
    /// it start; a worker never publishes one. The read also loads the versions the schedule plan resolves.
    /// </summary>
    private async Task ValidateCalendarsAsync(
        string ns,
        int namespaceId,
        ImmutableArray<JobDescriptor> descriptors,
        IReadOnlyCollection<StoredDefinitionContract> stored,
        CancellationToken ct
    )
    {
        var env = _options.Value.EnvironmentName;
        // A retired definition keeps its schedules as the retire left them, so this start does not judge its calendars.
        // They are loaded all the same: registration reactivates a retired definition a build declares again, and
        // the plan then resolves its schedules.
        var retired = stored
            .Where(s => s.Status == DefinitionStatusCode.Retired)
            .Select(s => s.DefinitionName)
            .ToHashSet(StringComparer.Ordinal);
        var schedules = descriptors
            .Where(d => !d.Schedules.IsDefaultOrEmpty)
            .SelectMany(d =>
                d.Schedules.Where(s => ScheduleEnvironment.IsActiveIn(s.Environments, env))
                    .Select(s =>
                        (
                            Path: $"{ns}/{d.JobName}/{s.ScheduleName}",
                            Retired: retired.Contains(d.JobName),
                            Timing: new ScheduleTiming(s.Expression, s.ExpressionKind, null, null, s.AvailabilityCalendarName)
                        )
                    )
            )
            .ToList();
        await calendars.PreloadAsync(namespaceId, schedules.SelectMany(s => s.Timing.CalendarNames()), ct);

        var problems = schedules
            .Where(s => !s.Retired)
            .SelectMany(s => calendars.Source(namespaceId, s.Timing).Problems().Select(problem => $"Schedule '{s.Path}': {problem}"))
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Worker namespace '{ns}' cannot start: a schedule names a calendar that is not ready. Publish each calendar "
                    + "before the workers that name it start; nothing was registered."
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, problems)
            );
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
                    $"Job '{descriptor.JobName}' declares TenantRequirement = Required together with [Schedule]. "
                        + "Recurring slots are enqueued without a tenant, so a scheduled definition cannot require one."
                );
            }
        }
    }

    /// <summary>
    /// Definition-sourced recurring slots: reconcile each scheduled definition's slot + schedules against persisted
    /// cursors at startup. The upsert set is the union of {descriptors that declare >= 1 [Schedule]} and {definitions
    /// with persisted schedule state}, so removed schedules can pause an existing slot, while ordinary non-scheduled jobs
    /// are never touched; a <paramref name="retired"/> definition keeps its schedules as the retire left them. The
    /// registration stores the hash of <paramref name="catalogParts"/> and the plan's declarations on the namespace.
    /// </summary>
    private async Task ReconcileSchedulesAsync(
        string ns,
        int namespaceId,
        Dictionary<int, JobDescriptor> descriptorById,
        List<string?> catalogParts,
        string? registeredCatalogHash,
        HashSet<int> retired,
        CancellationToken ct
    )
    {
        var stored = (await _schedules.ListScheduleStatesAsync(namespaceId, ct)).States;

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
        foreach (var (defId, descriptor) in descriptorById)
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

        // The calendars the plan resolves were read and checked before the first catalog write (ValidateCalendarsAsync).
        // Nothing about the resolution is stored: the admitter resolves each name afresh at every evaluation.
        var nowUtc = await _clock.GetUtcNowAsync(ct);
        var definitions = new List<DefinitionSchedules>(inScope.Count);

        foreach (var defId in inScope.Order())
        {
            if (!descriptorById.TryGetValue(defId, out var descriptor) || retired.Contains(defId))
            {
                // Persisted schedules for a definition no longer in this manifest, or one still retired: leave its
                // slot alone (definition retirement is a separate concern, not schedule reconciliation).
                continue;
            }

            var declared = descriptor.Schedules.IsDefaultOrEmpty
                ? (List<ScheduleDescriptor>)[]
                : descriptor.Schedules.Where(s => ScheduleEnvironment.IsActiveIn(s.Environments, env)).ToList();
            var storedForDef = storedByDefinition.TryGetValue(defId, out var byName)
                ? byName
                : (IReadOnlyDictionary<string, StoredScheduleState>)new Dictionary<string, StoredScheduleState>();

            var slotSchedules = ScheduleRegistrationPlan.Plan(declared, storedForDef, nowUtc, t => calendars.Source(namespaceId, t));

            // Registration leaves a held slot as it stands; say so at every start.
            var slot = storedForDef.Values.FirstOrDefault();
            if (declared.Count > 0 && slot?.SlotStatus == JobStatusCode.Paused)
            {
                ActaLog.RecurringJobPaused(_log, ns, descriptor.JobName);
            }

            var (inputFormatId, inputBytes) = JobDefinitionRegistration.DefaultInputOf(descriptor, _serializers);

            definitions.Add(
                new DefinitionSchedules(
                    NamespaceId: namespaceId,
                    DefinitionId: defId,
                    InputFormatId: inputFormatId,
                    Input: inputBytes,
                    EventRecording: descriptor.EventRecording,
                    ScheduledDelivery: descriptor.ScheduledDelivery,
                    Schedules: slotSchedules
                )
            );
        }

        var catalogHash = CatalogHashOf(catalogParts, definitions);
        if (
            catalogHash == registeredCatalogHash
            && definitions.All(d =>
                IsRegistered(d, storedByDefinition.GetValueOrDefault(d.DefinitionId), descriptorById[d.DefinitionId].Priority)
            )
        )
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

        await _schedules.RegisterSchedulesAsync(new RegisterSchedulesCommand(namespaceId, definitions, slotRefs, catalogHash), ct);
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
                    s.MaxOutstanding.ToString(c),
                    ((byte)s.FullBehavior).ToString(c),
                    s.ExpireAfterSeconds?.ToString(c),
                    s.ExpireAction is { } expireAction ? ((byte)expireAction).ToString(c) : null,
                    s.AvailabilityCalendarName,
                    s.StartAtUtc?.Ticks.ToString(c),
                    s.EndBeforeUtc?.Ticks.ToString(c),
                ]);
            }
        }

        return CatalogHash.Of([.. catalogParts]);
    }

    /// <summary>
    /// Whether <c>register_schedules</c> would write nothing for <paramref name="plan"/>: each planned schedule is stored
    /// with its planned cursor, capacity, expiry, and availability calendar, no stored schedule is left to orphan, and the
    /// slot, when there is one, either rests where registration leaves it (in flight, waiting, finished, or held) or
    /// already stands at the planned status, next run, and priority.
    /// </summary>
    internal static bool IsRegistered(DefinitionSchedules plan, Dictionary<string, StoredScheduleState>? stored, JobPriorityCode priority)
    {
        if (
            stored is null
            || stored.Count != plan.Schedules.Count
            || !plan.Schedules.All(s =>
                stored.TryGetValue(s.ScheduleName, out var st)
                && st.NextOccurrenceAtUtc == s.NextOccurrenceAtUtc
                && st.TimingFingerprint == s.TimingFingerprint
                && st.MaxOutstanding == s.MaxOutstanding
                && st.FullBehavior == s.FullBehavior
                && st.ExpireAfterSeconds == s.ExpireAfterSeconds
                && st.ExpireAction == s.ExpireAction
                && st.AvailabilityCalendarName == s.AvailabilityCalendarName
            )
        )
        {
            return false;
        }

        // The declaration's priority reaches only an Idle slot; any other keeps its own until it is Idle again.
        var slot = stored.Values.First();
        return slot.SlotStatus != JobStatusCode.Idle || slot.SlotPriority == priority;
    }
}

/// <summary>
/// Which jobs of the generated <c>ActaJobs</c> framework manifest a worker namespace registers, and whether its worker
/// runs crash recovery. <see cref="JobsOptions.RunSystemJobs"/> brings the maintenance set; an outbox relay brings
/// <c>sys.outbox</c>, which is opt-in and registers only through <c>AddOutboxRelay</c>. Either one needs crash recovery
/// and <c>sys.alerts</c>, so a relay brings them even when automatic registration is off, and never forces the
/// unrelated <c>sys.retention</c>.
/// </summary>
internal static class SystemJobs
{
    public static IReadOnlySet<string> For(bool runSystemJobs, bool hasRelay)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (runSystemJobs || hasRelay)
        {
            names.Add("sys.alerts");
        }

        if (runSystemJobs)
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
    public static bool RunsRecovery(bool runSystemJobs, bool hasRelay) => runSystemJobs || hasRelay;
}

/// <summary>
/// Environment gating for declared schedules. A <c>[Schedule]</c> whose <c>Environments</c> is empty
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
