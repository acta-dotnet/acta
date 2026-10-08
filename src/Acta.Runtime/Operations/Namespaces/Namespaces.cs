namespace Acta.Runtime;

/// <summary><see cref="INamespaces"/>, one line per operation.</summary>
internal sealed class Namespaces(
    ListNamespacesHandler listNamespaces,
    ListNamespaceItemsHandler listNamespaceItems,
    SuspendNamespaceHandler suspendNamespace,
    ResumeNamespaceHandler resumeNamespace,
    UpdateNamespaceHandler updateNamespace
) : INamespaces
{
    public ValueTask<PagedResult<string>> ListNamesAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        listNamespaces.HandleAsync(query, ct);

    public ValueTask<PagedResult<NamespaceListItem>> ListAsync(ListNamespaceItemsQuery query, CancellationToken ct = default) =>
        listNamespaceItems.HandleAsync(query, ct);

    public ValueTask<SuspendNamespaceResponse> SuspendAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => suspendNamespace.HandleAsync(new SuspendNamespaceCommand(name, reasonMessage, actorKey), ct);

    public ValueTask<ResumeNamespaceResponse> ResumeAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) => resumeNamespace.HandleAsync(new ResumeNamespaceCommand(name, reasonMessage, actorKey), ct);

    public ValueTask<UpdateNamespaceResponse> UpdateAsync(
        string name,
        int expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    ) =>
        updateNamespace.HandleAsync(new UpdateNamespaceCommand(name, expectedVersion, ownerTeam, description, reasonMessage, actorKey), ct);
}
