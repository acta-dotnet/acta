using Acta.Features.Jobs;
using Acta.Features.Shared;

namespace Acta.Features.Tenants;

/// <summary>
/// Persistence port for the tenants control plane: the idempotent registration upsert, the
/// key-ordered list, and the operator control verbs. Every control verb returns exactly one
/// (action, version) outcome row and emits its audit event (to the sys namespace) in the same
/// transaction; requests arrive validated with keys already canonicalized.
/// </summary>
internal interface ITenantStore
{
    /// <summary>Upserts one tenant by key and returns its id; unchanged metadata writes nothing.</summary>
    Task<int> RegisterTenantAsync(RegisterTenantCommand command, CancellationToken ct);

    /// <summary>One keyset page of tenants ordered by key ascending plus an opt-in total.</summary>
    Task<TenantPage> ListTenantsAsync(TenantPageRequest request, CancellationToken ct);

    /// <summary>Suspends a tenant; idempotent on an already-suspended row.</summary>
    Task<AdminControlOutcome> SuspendTenantAsync(TenantControlCommand command, CancellationToken ct);

    /// <summary>Resumes a tenant; idempotent on an already-active row.</summary>
    Task<AdminControlOutcome> ResumeTenantAsync(TenantControlCommand command, CancellationToken ct);

    /// <summary>Writes display name and description behind a version CAS.</summary>
    Task<AdminControlOutcome> UpdateTenantMetadataAsync(UpdateTenantMetadataCommand command, CancellationToken ct);
}

/// <summary>Canonicalized registration upsert for one tenant.</summary>
internal sealed record RegisterTenantCommand(string TenantKey, string? DisplayName, string? Description, TenantStatusCode Status);

/// <summary>Validated, cursor-decoded request for one tenant page; Take carries the peek-ahead row.
/// SearchPattern is a pre-lowercased '%term%' LIKE pattern (or null); Status filters by state (or null).</summary>
internal sealed record TenantPageRequest(
    string? SearchPattern,
    TenantStatusCode? Status,
    string? CursorTenantKey,
    int Take,
    bool IncludeTotal,
    string? TagFiltersJson = null
);

/// <summary>One page of tenant list items plus the opt-in total.</summary>
internal sealed record TenantPage(IReadOnlyList<TenantListItem> Rows, long? Total);

/// <summary>An operator control verb aimed at one tenant.</summary>
internal sealed record TenantControlCommand(string Key, JobControlActor Actor, string? ReasonMessage);

/// <summary>Version-guarded metadata write for one tenant.</summary>
internal sealed record UpdateTenantMetadataCommand(
    string TenantKey,
    string? DisplayName,
    string? Description,
    int ExpectedVersion,
    JobControlActor Actor,
    string? ReasonMessage
);
