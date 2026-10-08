namespace Acta;

/// <summary><see cref="INamespaces"/>, one line per operation.</summary>
internal sealed class Namespaces(
    ListNamespaceNamesHandler listNamespaceNames,
    ListNamespacesHandler listNamespaces,
    SuspendNamespaceHandler suspendNamespace,
    ResumeNamespaceHandler resumeNamespace,
    UpdateNamespaceHandler updateNamespace
) : INamespaces
{
    public ValueTask<ListNamespaceNamesResponse> ListNamesAsync(ListNamespaceNamesQuery query, CancellationToken ct = default) =>
        listNamespaceNames.HandleAsync(query, ct);

    public ValueTask<ListNamespacesResponse> ListAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        listNamespaces.HandleAsync(query, ct);

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
