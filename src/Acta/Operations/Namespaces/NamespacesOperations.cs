namespace Acta;

/// <summary><see cref="INamespaces"/>, one line per operation.</summary>
internal sealed class NamespacesOperations(
    ListNamespacesHandler listNamespaces,
    SuspendNamespaceHandler suspendNamespace,
    ResumeNamespaceHandler resumeNamespace,
    UpdateNamespaceHandler updateNamespace
) : INamespaces
{
    public ValueTask<ListNamespacesResponse> ListAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        listNamespaces.HandleAsync(query, ct);

    public ValueTask<SuspendNamespaceResponse> SuspendAsync(
        string namespaceName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => suspendNamespace.HandleAsync(new SuspendNamespaceCommand(namespaceName, reasonMessage, actor), ct);

    public ValueTask<ResumeNamespaceResponse> ResumeAsync(
        string namespaceName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resumeNamespace.HandleAsync(new ResumeNamespaceCommand(namespaceName, reasonMessage, actor), ct);

    public ValueTask<UpdateNamespaceResponse> UpdateAsync(
        string namespaceName,
        int expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        updateNamespace.HandleAsync(
            new UpdateNamespaceCommand(namespaceName, expectedVersion, ownerTeam, description, reasonMessage, actor),
            ct
        );
}
