namespace Acta;

/// <summary>
/// Namespaces domain: read one registered namespace or list them, plus operator admin (suspend/resume/update). Reached
/// through <see cref="IActaOperations.Namespaces"/>. The seeded sys namespace (id 1) cannot be suspended or edited.
/// </summary>
public interface INamespaces
{
    /// <summary>Point-read one registered namespace by name; null when it does not exist.</summary>
    ValueTask<GetNamespaceResponse?> GetAsync(string namespaceName, CancellationToken ct = default);

    /// <summary>List registered namespaces alphabetically with their status, owner team, description, and version for the admin page.</summary>
    ValueTask<ListNamespacesResponse> ListAsync(ListNamespacesQuery query, CancellationToken ct = default);

    /// <summary>Suspend a namespace by name (status-only, idempotent). Emits namespace.suspended.</summary>
    ValueTask<SuspendNamespaceResponse> SuspendAsync(
        string namespaceName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>Resume a suspended namespace by name (status-only, idempotent). Emits namespace.resumed.</summary>
    ValueTask<ResumeNamespaceResponse> ResumeAsync(
        string namespaceName,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update a namespace owner team / description with a version CAS. Null clears the field. Emits namespace.updated.
    /// A value the build declares on its worker is the build's, and the next deploy rewrites it; one the build does not
    /// declare is the operator's and survives every deploy.
    /// </summary>
    ValueTask<UpdateNamespaceResponse> UpdateAsync(
        string namespaceName,
        long expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actor = null,
        CancellationToken ct = default
    );
}
