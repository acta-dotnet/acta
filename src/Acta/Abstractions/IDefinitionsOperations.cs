namespace Acta;

/// <summary><see cref="IDefinitions"/> plus the catalog write and read that worker startup and the policy reload tick run.</summary>
internal interface IDefinitionsOperations : IDefinitions
{
    /// <summary>
    /// Registers a namespace's whole definitions set and answers each descriptor's definition id; a steady-state restart
    /// issues zero writes and takes no locks. The database keeps the per-row generation/hash gate, so it holds under
    /// concurrent registrars.
    /// </summary>
    Task<RegisterDefinitionsResponse> RegisterDefinitionsAsync(RegisterDefinitionsCommand command, CancellationToken ct);

    /// <summary>
    /// The full catalog of every definition in a namespace, for worker startup and the policy reload tick: identity,
    /// generation, hash, status, contract, modification time, and effective policy.
    /// </summary>
    Task<GetDefinitionContractsResponse> GetDefinitionContractsAsync(int namespaceId, CancellationToken ct);
}
