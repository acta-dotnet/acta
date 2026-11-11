using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace Acta;

/// <summary>
/// Shared mutable worker state threaded between the initializer (writer) and the claim/dispatch loop and dispatcher
/// (readers): populated by <see cref="WorkerRuntimeInitializer"/> at startup and consulted on the hot path for the
/// worker identity, the descriptor for a claimed job, and whether a claimed job is a recurring slot fire. Every
/// collection stays empty in enqueue-only mode (no <see cref="WorkerRegistration"/>).
/// </summary>
internal sealed class WorkerContext(WorkerRegistration? workerRegistration)
{
    public WorkerRegistration? WorkerRegistration { get; } = workerRegistration;

    /// <summary>The worker namespace's id, set when the worker starts.</summary>
    public int? NamespaceId { get; set; }

    /// <summary>This process's row in <c>workers</c>, set when the worker starts and cleared when its catalog is refused.</summary>
    public int? WorkerId { get; set; }

    /// <summary>
    /// definitions.definition_id to JobDescriptor for hot-path claim dispatch. Built in InitializeAsync from the
    /// stored definitions; consulted in RunOnceAsync to resolve the descriptor by ClaimedJob.DefinitionId, keeping
    /// <c>claim_jobs</c> free of any JOIN to definitions. Each descriptor carries the definition's effective
    /// (override-or-default) policy. Concurrent because the definition-policy reload tick re-overlays entries while
    /// executor threads read them.
    /// </summary>
    public ConcurrentDictionary<int, JobDescriptor> DescriptorByDefinitionId { get; } = new();

    /// <summary>
    /// definitions.definition_id to effective retention in seconds for each definition the last catalog read, the start's or a heartbeat's, found
    /// Retired, whether or not this deployment carries its handler. Their jobs never start another attempt or re-arm.
    /// Replaced whole on each read, so a definition a build made Active again leaves it.
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

    /// <summary>The started worker's namespace with its id: how a test and the testing host address a runtime.</summary>
    public IReadOnlyDictionary<string, int> RegisteredNamespaceIds =>
        WorkerRegistration is { NamespaceName: var name } && NamespaceId is { } id
            ? new Dictionary<string, int>(StringComparer.Ordinal) { [name] = id }
            : FrozenDictionary<string, int>.Empty;

    /// <summary>The started worker's namespace id and worker id.</summary>
    public (int NamespaceId, int WorkerId) Worker =>
        this is { NamespaceId: { } namespaceId, WorkerId: { } workerId }
            ? (namespaceId, workerId)
            : throw new InvalidOperationException(
                WorkerRegistration is null
                    ? "Worker mode required. Call j.Run<TManifest>(jobNamespace, ...) inside UseActa."
                    : $"The worker for namespace '{WorkerRegistration.NamespaceName}' has not started. Call InitializeAsync first."
            );

    /// <summary>
    /// <see cref="Worker"/> for a caller that names the namespace, the single-shot path: a name that is not this
    /// runtime's is refused, so a host of several runtimes never runs one for another.
    /// </summary>
    public (int NamespaceId, int WorkerId) ResolveWorker(string namespaceName) =>
        WorkerRegistration is { NamespaceName: var own } && own != namespaceName
            ? throw new InvalidOperationException(
                $"Namespace mismatch: this runtime is registered as the worker for '{own}', not '{namespaceName}'."
            )
            : Worker;
}
