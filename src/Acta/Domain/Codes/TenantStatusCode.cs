using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Tenant state machine. 10-spaced ids; live states 10..80.
/// </summary>
[JsonConverter(typeof(TenantStatusCodeJsonConverter))]
[CodeKind("tenant-status")]
public enum TenantStatusCode : byte
{
    [Code("unspecified", "Tenant status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Tenant name resolves at enqueue; enqueue allowed.")]
    Active = 10,

    [Code(
        "suspended",
        "Admission suspended: new enqueues naming the tenant name are rejected, while already admitted jobs keep running and may expand through inherited children. Applies to enqueue transactions beginning after the suspend commits. Reversible (billing/abuse hold, admin pause)."
    )]
    Suspended = 20,
}
