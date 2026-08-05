namespace Acta;

/// <summary>
/// Namespaces domain: list registered namespaces plus operator admin (suspend/resume/metadata). Reached
/// through <see cref="IActaOperations.Namespaces"/>. The seeded sys namespace (id 1) cannot be suspended or edited.
/// </summary>
public interface INamespaces
{
    /// <summary>List registered namespace names alphabetically, optionally restricted to a name prefix.</summary>
    ValueTask<PagedResult<string>> ListAsync(ListNamespacesQuery query, CancellationToken ct = default);

    /// <summary>List registered namespaces alphabetically with their status, owner team, description, and version for the admin page.</summary>
    ValueTask<PagedResult<NamespaceListItem>> ListItemsAsync(ListNamespacesQuery query, CancellationToken ct = default);

    /// <summary>Suspend a namespace by name (status-only, idempotent). Emits namespace.suspended. Rejects the sys namespace.</summary>
    ValueTask<AdminControlResult> SuspendAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>Resume a suspended namespace by name (status-only, idempotent). Emits namespace.resumed. Rejects the sys namespace.</summary>
    ValueTask<AdminControlResult> ResumeAsync(
        string name,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );

    /// <summary>
    /// Update a namespace owner team / description with a version CAS. Null clears the field.
    /// Emits namespace.metadata-changed. Rejects the sys namespace.
    /// </summary>
    ValueTask<AdminControlResult> UpdateMetadataAsync(
        string name,
        string? ownerTeam,
        string? description,
        int expectedVersion,
        string? reasonMessage = null,
        string? actorKey = null,
        CancellationToken ct = default
    );
}
