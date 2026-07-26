using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Tenant state machine. 10-spaced ids; live states 10..80. Id 90 is reserved for an
/// Archived (hidden/historical) status, to be added when the dashboard/API need historical tenant
/// cleanup.
/// </summary>
[JsonConverter(typeof(TenantStatusCodeJsonConverter))]
[CodeKind("tenant-status")]
public enum TenantStatusCode : byte
{
    [Code("active", "Tenant key resolves at enqueue; enqueue allowed.")]
    Active = 10,

    [Code(
        "suspended",
        "Admission suspended: new enqueues naming the tenant key are rejected, while already admitted jobs keep running and may expand through inherited children. Applies to enqueue transactions beginning after the suspend commits. Reversible (billing/abuse hold, admin pause)."
    )]
    Suspended = 20,
}
