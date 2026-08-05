using Acta.Runtime.Kernel;
using Acta.Runtime.Modules.Execution.Api;

namespace Acta.Runtime.Modules.Execution.Tenants;

/// <summary>
/// Persistence port for the tenants control plane: the insert-or-return-existing registration, the
/// key-ordered list, and the operator control verbs. Every control verb returns exactly one
/// (action, version) outcome row and emits its audit event (to the sys namespace) in the same
/// transaction; requests arrive validated with keys already canonicalized.
/// </summary>
internal interface ITenantStore
{
    /// <summary>Inserts one Active tenant by key or returns the existing id; existing rows are never modified.</summary>
    Task<int> RegisterTenantAsync(RegisterTenantCommand command, CancellationToken ct);

    /// <summary>Point-reads one tenant by key or id; null when it does not exist.</summary>
    Task<TenantListItem?> GetTenantAsync(TenantPointLookup lookup, CancellationToken ct);

    /// <summary>One keyset page of tenants ordered by key ascending plus an opt-in total.</summary>
    Task<TenantPage> ListTenantsAsync(TenantPageRequest request, CancellationToken ct);

    /// <summary>Suspends a tenant; idempotent on an already-suspended row.</summary>
    Task<AdminControlOutcome> SuspendTenantAsync(TenantControlCommand command, CancellationToken ct);

    /// <summary>Resumes a tenant; idempotent on an already-active row.</summary>
    Task<AdminControlOutcome> ResumeTenantAsync(TenantControlCommand command, CancellationToken ct);

    /// <summary>Writes display name and description behind a version CAS.</summary>
    Task<AdminControlOutcome> UpdateTenantAsync(UpdateTenantCommand command, CancellationToken ct);
}

/// <summary>Canonicalized registration request for one tenant; applies only when the key is new.</summary>
internal sealed record RegisterTenantCommand(string TenantKey, string? DisplayName, string? Description);

/// <summary>Point-lookup address for one tenant: exactly one of the canonical key or the internal id.</summary>
internal readonly record struct TenantPointLookup(string? TenantKey, int? TenantId);

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

/// <summary>Version-guarded update for one tenant.</summary>
internal sealed record UpdateTenantCommand(
    string TenantKey,
    string? DisplayName,
    string? Description,
    int ExpectedVersion,
    JobControlActor Actor,
    string? ReasonMessage
);
