using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Whether jobs of a definition must, may, or must not carry a tenant. Enforced at the enqueue
/// boundary in the database, so every write path (API, outbox relay, any other client) receives
/// the same rule.
/// </summary>
[JsonConverter(typeof(JobTenantRequirementCodeJsonConverter))]
[CodeKind("job-tenant-requirement")]
public enum JobTenantRequirementCode : byte
{
    /// <summary>The definition accepts tenant-scoped and tenant-less jobs alike.</summary>
    [Code("optional", "Jobs may carry a tenant or not; no enqueue-time tenant rule applies.")]
    Optional = 0,

    /// <summary>
    /// Every job of this definition must be tenant-scoped: a root enqueue must name a TenantKey, and
    /// a child must name one or inherit a tenant from its parent.
    /// </summary>
    [Code("required", "Every enqueue must carry a tenant, by explicit TenantKey or parent inheritance; a tenant-less enqueue is rejected.")]
    Required = 10,

    /// <summary>
    /// Jobs of this definition are never about a tenant: an explicit TenantKey is rejected, and a
    /// child of a tenant-scoped parent is admitted with its inherited tenant suppressed to NULL.
    /// </summary>
    [Code("forbidden", "An explicit TenantKey is rejected and a child never inherits its parent's tenant; rows always store tenant NULL.")]
    Forbidden = 20,
}
