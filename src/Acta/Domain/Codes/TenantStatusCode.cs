using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Whether enqueues that name a tenant are accepted.</summary>
/// <remarks>Ids are spaced by 10, with live states in 10..80.</remarks>
[JsonConverter(typeof(TenantStatusCodeJsonConverter))]
[CodeKind("tenant-status")]
public enum TenantStatusCode : byte
{
    [Code("unspecified", "Tenant status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Enqueues that name the tenant are accepted.")]
    Active = 10,

    [Code(
        "suspended",
        "New enqueues that name the tenant are rejected, while its admitted jobs keep running and may still start child jobs. It applies to enqueues that begin after the suspension commits, and an operator can resume the tenant."
    )]
    Suspended = 20,
}
