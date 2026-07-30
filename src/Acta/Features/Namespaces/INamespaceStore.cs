using Acta.Features.Jobs;
using Acta.Features.Shared;

namespace Acta.Features.Namespaces;

/// <summary>
/// Persistence port for the namespaces control plane: the alphabetical name list and the operator
/// control verbs. Namespace creation is not here - the worker's start upsert owns it atomically.
/// Every control verb returns exactly one (action, version) outcome row and emits its audit event in
/// the same transaction; requests arrive validated and canonicalized.
/// </summary>
internal interface INamespaceStore
{
    /// <summary>One keyset page of namespace names ascending, optionally prefix-filtered, plus an opt-in total.</summary>
    Task<NamespacePage> ListNamespacesAsync(NamespacePageRequest request, CancellationToken ct);

    /// <summary>One keyset page of namespace admin rows (status, metadata, version) ascending, optionally prefix-filtered, plus an opt-in total.</summary>
    Task<NamespaceItemPage> ListNamespaceItemsAsync(NamespacePageRequest request, CancellationToken ct);

    /// <summary>Suspends a namespace; idempotent on an already-suspended row.</summary>
    Task<AdminControlOutcome> SuspendNamespaceAsync(NamespaceControlCommand command, CancellationToken ct);

    /// <summary>Resumes a namespace; idempotent on an already-active row.</summary>
    Task<AdminControlOutcome> ResumeNamespaceAsync(NamespaceControlCommand command, CancellationToken ct);

    /// <summary>Writes owner team and description behind a version CAS.</summary>
    Task<AdminControlOutcome> UpdateNamespaceMetadataAsync(UpdateNamespaceMetadataCommand command, CancellationToken ct);
}

/// <summary>Validated, cursor-decoded request for one page of namespace names; Take carries the peek-ahead row.
/// Status is honored only by the admin-row list (the name list projects no status).</summary>
internal sealed record NamespacePageRequest(
    string? NameSearch,
    JobNamespaceStatusCode? Status,
    string? CursorName,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of namespace names plus the opt-in prefix-wide total.</summary>
internal sealed record NamespacePage(IReadOnlyList<string> Rows, long? Total);

/// <summary>One page of namespace admin rows plus the opt-in prefix-wide total.</summary>
internal sealed record NamespaceItemPage(IReadOnlyList<NamespaceListItem> Rows, long? Total);

/// <summary>An operator control verb aimed at one namespace.</summary>
internal sealed record NamespaceControlCommand(string Key, JobControlActor Actor, string? ReasonMessage);

/// <summary>Version-guarded metadata write for one namespace.</summary>
internal sealed record UpdateNamespaceMetadataCommand(
    string Name,
    string? OwnerTeam,
    string? Description,
    int ExpectedVersion,
    JobControlActor Actor,
    string? ReasonMessage
);
