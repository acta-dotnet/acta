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
    /// definitions.definition_id to JobDescriptor for hot-path claim dispatch. Built in InitializeAsync after
    /// RegisterDefinitionsAsync returns the DB-assigned ids; consulted in RunOnceAsync to resolve the
    /// descriptor by ClaimedJob.DefinitionId, keeping <c>claim_jobs</c> free of any JOIN to
    /// definitions. Each descriptor carries the definition's effective (override-or-default)
    /// policy. Concurrent because the definition-policy reload tick re-overlays entries while
    /// executor threads read them.
    /// </summary>
    public ConcurrentDictionary<int, JobDescriptor> DescriptorByDefinitionId { get; } = new();

    /// <summary>
    /// definitions.definition_id to effective retention in seconds for each definition the heartbeat's last catalog read found
    /// Retired, whether or not this deployment carries its handler. Their jobs never start another attempt or re-arm.
    /// Replaced whole on each read, so a definition a newer build made Active again leaves it.
    /// </summary>
    public IReadOnlyDictionary<int, int> RetentionSecondsByRetiredDefinitionId { get; private set; } = new Dictionary<int, int>();

    /// <summary>Replaces the retired definitions and republishes the claim exclusion without them.</summary>
    public void ReplaceRetiredDefinitions(IReadOnlyDictionary<int, int> retentionSecondsByDefinitionId)
    {
        RetentionSecondsByRetiredDefinitionId = retentionSecondsByDefinitionId;
        RebuildUnsupportedSnapshot();
    }

    /// <summary>
    /// definitions.definition_id values this worker bounced for want of a handler, and therefore stops claiming.
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
    /// The excluded set less the retired definitions, as an array to bind into a claim, rebuilt on each
    /// change of either. A retired definition's job must still be claimed, so the executor lands it
    /// Cancelled rather than leaving it Ready for no worker. Readers take the whole array, so a claim
    /// binds a set that was coherent at some instant rather than one enumerated while a bounce was adding to it.
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
    /// Republishes the snapshot, reading its inputs and storing it under one lock: two concurrent changes
    /// could otherwise interleave so the one that read the older inputs publishes last, hiding a change.
    /// </summary>
    private void RebuildUnsupportedSnapshot()
    {
        lock (_unsupportedGate)
        {
            _unsupportedDefinitionIdsSnapshot =
            [
                .. UnsupportedDefinitionIds.Keys.Where(id => !RetentionSecondsByRetiredDefinitionId.ContainsKey(id)),
            ];
        }
    }

    /// <summary>Whether this worker runs crash recovery for its namespace, decided at registration.</summary>
    public bool RunsRecovery { get; set; }

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
