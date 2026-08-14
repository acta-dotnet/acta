namespace Acta.Runtime.Modules.Execution.Namespaces;

/// <summary>
/// <see cref="INamespaces"/> implementation: a thin adapter over the Namespaces feature service,
/// which owns validation, the sys rejection, actor stamping, and the store delegation.
/// </summary>
internal sealed class NamespacesApi(NamespacesService namespaces) : INamespaces
{
    public ValueTask<PagedResult<string>> ListAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        namespaces.ListAsync(query, ct);

    public ValueTask<PagedResult<NamespaceListItem>> ListItemsAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        namespaces.ListItemsAsync(query, ct);

    public ValueTask<AdminControlResult> SuspendAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => namespaces.SuspendAsync(name, reasonMessage, actorKey, ct);

    public ValueTask<AdminControlResult> ResumeAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => namespaces.ResumeAsync(name, reasonMessage, actorKey, ct);

    public ValueTask<AdminControlResult> UpdateAsync(
        string name,
        int expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => namespaces.UpdateAsync(name, ownerTeam, description, expectedVersion, reasonMessage, actorKey, ct);
}
