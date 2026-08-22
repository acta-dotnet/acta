using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Definitions;

/// <summary>
/// Persistence port for the definitions catalog: the contract/policy read used by worker startup and
/// the policy reload tick, the dashboard detail and grid reads, the whole-namespace registration
/// upsert, and the operator override write. Requests arrive validated with rows already resolved and
/// cursors already decoded; implementations own command creation, parameter binding, batch shapes,
/// row mapping, and transactions.
/// </summary>
internal interface IDefinitionStore
{
    /// <summary>
    /// The full catalog of every definition in a namespace: identity, generation, hash, status,
    /// contract, modification time, and effective policy.
    /// </summary>
    Task<IReadOnlyList<StoredDefinitionContract>> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct);

    /// <summary>One fully-projected definition row by surrogate id, or null when no row matches.</summary>
    ValueTask<JobDefinitionDetail?> GetDefinitionAsync(int definitionId, CancellationToken ct);

    /// <summary>
    /// One keyset page of grid-shaped definition rows ordered namespace, name, id ascending plus an
    /// opt-in filter-wide total, fetched in a single round trip.
    /// </summary>
    Task<DefinitionPage> ListDefinitionsAsync(DefinitionPageRequest request, CancellationToken ct);

    /// <summary>
    /// Upserts the namespace's whole definitions set in one round trip and returns a name-to-id map
    /// with exactly one row per input definition. The per-row generation/hash gate and
    /// retire-by-absence live in the database so they hold under concurrent registrars.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct);

    /// <summary>
    /// Applies an operator's policy-override set: writes only the override columns, version-guarded,
    /// and emits the definition-scoped policy-changed event in the same transaction.
    /// </summary>
    Task<DefinitionOverrideOutcome> SetDefinitionOverridesAsync(SetDefinitionOverridesCommand command, CancellationToken ct);
}

/// <summary>Validated, cursor-decoded request for one definitions grid page.</summary>
internal sealed record DefinitionPageRequest(
    string? JobNamespace,
    string? NameSearch,
    JobDefinitionStatusCode? Status,
    string? CursorNamespaceName,
    string? CursorJobName,
    int? CursorId,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of grid-shaped definition rows plus the opt-in filtered total.</summary>
internal sealed record DefinitionPage(IReadOnlyList<JobDefinitionListItem> Rows, long? Total);

/// <summary>Resolved registration batch: policy defaults already applied and hashes computed.</summary>
internal sealed record RegisterDefinitionsCommand(int NamespaceId, DateTime ManifestGenerationUtc, IReadOnlyList<JobDefinitionRow> Rows);

/// <summary>Validated override write: canonicalized values plus the audit actor and reason.</summary>
internal sealed record SetDefinitionOverridesCommand(
    int DefinitionId,
    int ExpectedVersion,
    JobDefinitionPolicyOverrides Overrides,
    JobControlActor Actor,
    string? ReasonMessage
);
