using System.Collections.Concurrent;

namespace Acta;

/// <summary>
/// Shared mutable worker state threaded between the initializer (writer) and the claim/dispatch loop and dispatcher
/// (readers): populated by <see cref="WorkerRuntimeInitializer"/> and its <see cref="CatalogRegistrar"/> at startup and
/// consulted on the hot path for the worker identity, the descriptor for a claimed job, and whether a claimed job is a
/// recurring slot fire. Every collection stays empty in enqueue-only mode (no <see cref="WorkerRegistration"/>).
/// </summary>
internal sealed class WorkerContext(WorkerRegistration? workerRegistration)
{
    public WorkerRegistration? WorkerRegistration { get; } = workerRegistration;

    public Dictionary<string, int> NamespaceIds { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> WorkerIdByNamespace { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, int>> DefinitionIdsByNamespace { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// definitions.id to JobDescriptor for hot-path claim dispatch. Built in InitializeAsync after
    /// UpsertDefinitionsAsync returns the DB-assigned ids; consulted in RunOnceAsync to resolve the
    /// descriptor by ClaimedJob.DefinitionId, keeping <c>claim_batch</c> free of any JOIN to
    /// definitions. Each descriptor carries the definition's effective (override-or-default)
    /// policy. Concurrent because the definition-policy reload tick re-overlays entries while
    /// executor threads read them.
    /// </summary>
    public ConcurrentDictionary<int, JobDescriptor> DescriptorByDefinitionId { get; } = new();

    /// <summary>
    /// definitions.id values this worker bounced for want of a handler, and therefore stops claiming.
    /// Per process and never persisted: a build that carries the handler is a new process, so the set
    /// never shrinks in production and an operator needs no way to clear it. Claims are by namespace,
    /// so during a rolling deploy an old worker keeps claiming a definition only the new build can run
    /// and hands it straight back; learning the definition bounds that to the batch already claimed
    /// (every row of it still bounces, and only the first bounce warns): the next claim skips it.
    /// </summary>
    public ConcurrentDictionary<int, byte> UnsupportedDefinitionIds { get; } = new();

    private readonly object _unsupportedGate = new();

    private volatile int[] _unsupportedDefinitionIdsSnapshot = [];

    /// <summary>
    /// The excluded set as an array to bind into a claim, rebuilt on each change. Readers take the
    /// whole array, so a claim binds a set that was coherent at some instant rather than one
    /// enumerated while a bounce was adding to it.
    /// </summary>
    public int[] UnsupportedDefinitionIdsSnapshot => _unsupportedDefinitionIdsSnapshot;

    /// <summary>
    /// Excludes <paramref name="definitionId"/> from this worker's claims. Returns true only for the
    /// first worker thread to add it, which is what makes the bounce warn once per definition.
    /// </summary>
    public bool ExcludeDefinition(int definitionId)
    {
        if (!UnsupportedDefinitionIds.TryAdd(definitionId, 0))
        {
            return false;
        }
        RebuildUnsupportedSnapshot();
        return true;
    }

    /// <summary>
    /// Drops <paramref name="definitionId"/> from the excluded set so this worker claims it again.
    /// A test seam: production never forgets, because the deployment that carries the handler is a
    /// different process.
    /// </summary>
    public bool ForgetUnsupportedDefinition(int definitionId)
    {
        if (!UnsupportedDefinitionIds.TryRemove(definitionId, out _))
        {
            return false;
        }
        RebuildUnsupportedSnapshot();
        return true;
    }

    /// <summary>
    /// Republishes the snapshot, reading the set and storing it under one lock: two concurrent changes
    /// could otherwise interleave so the one that read the smaller set publishes last, hiding an id.
    /// </summary>
    private void RebuildUnsupportedSnapshot()
    {
        lock (_unsupportedGate)
        {
            _unsupportedDefinitionIdsSnapshot = [.. UnsupportedDefinitionIds.Keys];
        }
    }

    /// <summary>
    /// Slot job ids (one per recurring definition): those the startup schedule upsert returned, and any
    /// slot another runtime registered that an executor recognized on claiming it. Consulted on the
    /// execution hot path to branch a claimed slot fire into the recurring path.
    /// </summary>
    public ConcurrentDictionary<long, byte> RecurringSlotJobIds { get; } = new();

    /// <summary>
    /// The <c>sys.recovery</c> slot job id per namespace this worker registered, captured at the startup
    /// schedule upsert. The recovery monitor checks exactly this row and never recreates it: a slot an
    /// operator removed stays removed.
    /// </summary>
    public ConcurrentDictionary<int, long> RecoverySlotJobIdByNamespace { get; } = new();

    public IReadOnlyDictionary<string, int> RegisteredNamespaceIds => NamespaceIds;

    public bool TryGetDefinitionId(string namespaceName, string jobName, out int definitionId)
    {
        if (DefinitionIdsByNamespace.TryGetValue(namespaceName, out var byName) && byName.TryGetValue(jobName, out var id))
        {
            definitionId = id;
            return true;
        }
        definitionId = 0;
        return false;
    }

    /// <summary>
    /// Resolves and validates the (namespaceId, workerId) pair for the registered worker namespace.
    /// Shared by RunOnceAsync and the production claim loop.
    /// </summary>
    public (int NamespaceId, int WorkerId) ResolveWorker(string namespaceName)
    {
        if (WorkerRegistration is null)
        {
            throw new InvalidOperationException("Worker mode required. Call j.Run<TManifest>(namespaceName, ...) inside UseActa.");
        }
        if (namespaceName != WorkerRegistration.NamespaceName)
        {
            throw new InvalidOperationException(
                $"Namespace mismatch: this runtime is registered as the worker for '{WorkerRegistration.NamespaceName}', not '{namespaceName}'."
            );
        }
        if (!NamespaceIds.TryGetValue(namespaceName, out var namespaceId))
        {
            throw new InvalidOperationException($"Namespace '{namespaceName}' has no id yet. Call InitializeAsync before claiming.");
        }
        return !WorkerIdByNamespace.TryGetValue(namespaceName, out var workerId)
            ? throw new InvalidOperationException($"Worker id for namespace '{namespaceName}' not assigned. Call InitializeAsync first.")
            : ((int NamespaceId, int WorkerId))(namespaceId, workerId);
    }
}
