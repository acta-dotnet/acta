namespace Acta;

/// <summary>The Definitions operations: <see cref="IDefinitions"/> for applications, plus the engine's calls.</summary>
internal sealed class DefinitionsOperations(
    SetDefinitionOverridesHandler setDefinitionOverrides,
    RetireDefinitionHandler retireDefinition,
    GetDefinitionHandler getDefinition,
    ListDefinitionsHandler listDefinitions,
    RegisterDefinitionsHandler registerDefinitions,
    GetDefinitionContractsHandler getDefinitionContracts
) : IDefinitions
{
    public ValueTask<SetDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        setDefinitionOverrides.HandleAsync(
            new SetDefinitionOverridesCommand(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actor),
            ct
        );

    public ValueTask<RetireDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        int expectedVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => retireDefinition.HandleAsync(new RetireDefinitionCommand(jobNamespace, jobName, expectedVersion, reasonMessage, actor), ct);

    public ValueTask<GetDefinitionResponse?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        getDefinition.HandleAsync(new GetDefinitionQuery(jobNamespace, jobName), ct);

    public ValueTask<ListDefinitionsResponse> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        listDefinitions.HandleAsync(query, ct);

    /// <summary>
    /// Registers a namespace's whole definitions set and answers each descriptor's definition id; a steady-state restart
    /// issues zero writes and takes no locks. The database keeps the per-row generation/hash gate, so it holds under
    /// concurrent registrars.
    /// </summary>
    internal Task<RegisterDefinitionsResponse> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
        registerDefinitions.HandleAsync(command, ct);

    /// <summary>
    /// The full catalog of every definition in a namespace, for worker startup and the policy reload tick: identity,
    /// generation, hash, status, contract, modification time, and effective policy.
    /// </summary>
    internal Task<GetDefinitionContractsResponse> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
        getDefinitionContracts.HandleAsync(new GetDefinitionContractsQuery(namespaceId), ct);
}
