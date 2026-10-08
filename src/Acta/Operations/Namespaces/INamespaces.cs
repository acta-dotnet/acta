namespace Acta;

/// <summary>
/// Namespaces domain: list registered namespaces plus operator admin (suspend/resume/update). Reached
/// through <see cref="IActaOperations.Namespaces"/>. The seeded sys namespace (id 1) cannot be suspended or edited.
/// </summary>
public interface INamespaces
{
    /// <summary>List registered namespace names alphabetically, optionally restricted to names containing a fragment.</summary>
    ValueTask<PagedResult<string>> ListNamesAsync(ListNamespaceNamesQuery query, CancellationToken ct = default);

    /// <summary>List registered namespaces alphabetically with their status, owner team, description, and version for the admin page.</summary>
    ValueTask<PagedResult<NamespaceListItem>> ListAsync(ListNamespacesQuery query, CancellationToken ct = default);

    /// <summary>Suspend a namespace by name (status-only, idempotent). Emits namespace.suspended.</summary>
    ValueTask<SuspendNamespaceResponse> SuspendAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Resume a suspended namespace by name (status-only, idempotent). Emits namespace.resumed.</summary>
    ValueTask<ResumeNamespaceResponse> ResumeAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update a namespace owner team / description with a version CAS. Null clears the field.
    /// Emits namespace.updated.
    /// </summary>
    ValueTask<UpdateNamespaceResponse> UpdateAsync(
        string name,
        int expectedVersion,
        string? ownerTeam,
        string? description,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}
