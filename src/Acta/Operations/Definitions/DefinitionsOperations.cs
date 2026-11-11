using System.Data.Common;

namespace Acta;

/// <summary>The Definitions operations: <see cref="IDefinitions"/> for applications, plus the engine's calls.</summary>
internal sealed class DefinitionsOperations(
    UpdateDefinitionOverridesHandler updateDefinitionOverrides,
    RetireDefinitionHandler retireDefinition,
    GetDefinitionHandler getDefinition,
    ListDefinitionsHandler listDefinitions,
    GetDefinitionInputTemplateHandler getDefinitionInputTemplate,
    RegisterDefinitionsHandler registerDefinitions,
    ListDefinitionContractsHandler listDefinitionContracts
) : IDefinitions
{
    public ValueTask<UpdateDefinitionOverridesResponse> UpdateOverridesAsync(
        string jobNamespace,
        string jobName,
        long expectedVersion,
        DefinitionPolicyOverrides overrides,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        updateDefinitionOverrides.HandleAsync(
            new UpdateDefinitionOverridesCommand(jobNamespace, jobName, expectedVersion, overrides, reasonMessage, actor),
            ct
        );

    public ValueTask<RetireDefinitionResponse> RetireAsync(
        string jobNamespace,
        string jobName,
        long expectedVersion,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => retireDefinition.HandleAsync(new RetireDefinitionCommand(jobNamespace, jobName, expectedVersion, reasonMessage, actor), ct);

    public ValueTask<GetDefinitionResponse?> GetAsync(string jobNamespace, string jobName, CancellationToken ct = default) =>
        getDefinition.HandleAsync(new GetDefinitionQuery(jobNamespace, jobName), ct);

    public ValueTask<ListDefinitionsResponse> ListAsync(ListDefinitionsQuery query, CancellationToken ct = default) =>
        listDefinitions.HandleAsync(query, ct);

    public GetDefinitionInputTemplateResponse? GetInputTemplate(string jobNamespace, string jobName) =>
        getDefinitionInputTemplate.Handle(new GetDefinitionInputTemplateQuery(jobNamespace, jobName));

    /// <summary>
    /// Registers a namespace's whole definitions set and answers each descriptor's definition id; a steady-state restart
    /// issues zero writes and takes no locks. The database keeps the per-row hash gate, so a row two registrars agree
    /// on is written once.
    /// </summary>
    internal Task<RegisterDefinitionsResponse> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct) =>
        registerDefinitions.HandleAsync(command, ct);

    /// <summary>
    /// The full catalog of every definition in a namespace, for worker startup and the policy reload tick: identity,
    /// hash, status, contract, modification time, and effective policy.
    /// </summary>
    internal Task<ListDefinitionContractsResponse> ListDefinitionContractsAsync(int namespaceId, CancellationToken ct) =>
        listDefinitionContracts.HandleAsync(new ListDefinitionContractsQuery(namespaceId), ct);

    /// <summary>The same read inside <paramref name="transaction"/>, when one is given, so it sees what that transaction wrote.</summary>
    internal Task<ListDefinitionContractsResponse> ListDefinitionContractsAsync(
        int namespaceId,
        DbTransaction? transaction,
        CancellationToken ct
    ) => listDefinitionContracts.HandleAsync(new ListDefinitionContractsQuery(namespaceId, transaction), ct);
}
