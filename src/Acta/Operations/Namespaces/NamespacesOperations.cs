namespace Acta;

/// <summary><see cref="INamespaces"/>, one line per operation.</summary>
internal sealed class NamespacesOperations(
    GetNamespaceHandler getNamespace,
    ListNamespacesHandler listNamespaces,
    SuspendNamespaceHandler suspendNamespace,
    ResumeNamespaceHandler resumeNamespace,
    UpdateNamespaceHandler updateNamespace
) : INamespaces
{
    public ValueTask<GetNamespaceResponse?> GetAsync(string jobNamespace, CancellationToken ct = default) =>
        getNamespace.HandleAsync(new GetNamespaceQuery(jobNamespace), ct);

    public ValueTask<ListNamespacesResponse> ListAsync(ListNamespacesQuery query, CancellationToken ct = default) =>
        listNamespaces.HandleAsync(query, ct);

    public ValueTask<SuspendNamespaceResponse> SuspendAsync(
        string jobNamespace,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => suspendNamespace.HandleAsync(new SuspendNamespaceCommand(jobNamespace, reasonMessage, actor), ct);

    public ValueTask<ResumeNamespaceResponse> ResumeAsync(
        string jobNamespace,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) => resumeNamespace.HandleAsync(new ResumeNamespaceCommand(jobNamespace, reasonMessage, actor), ct);

    public ValueTask<UpdateNamespaceResponse> UpdateAsync(
        string jobNamespace,
        long expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    ) =>
        updateNamespace.HandleAsync(
            new UpdateNamespaceCommand(jobNamespace, expectedVersion, ownerTeam, description, reasonMessage, actor),
            ct
        );
}
