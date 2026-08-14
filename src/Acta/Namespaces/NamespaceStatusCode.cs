using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Namespace state machine. 10-spaced ids; live states 10..80, mirroring
/// <see cref="TenantStatusCode"/>. Id 90 is reserved for an Archived (hidden/historical)
/// status, for symmetry with <see cref="TenantStatusCode"/>'s reservation.
/// </summary>
[JsonConverter(typeof(NamespaceStatusCodeJsonConverter))]
[CodeKind("namespace-status")]
public enum NamespaceStatusCode : byte
{
    [Code("active", "Namespace resolves at enqueue; enqueue allowed.")]
    Active = 10,

    [Code("suspended", "Enqueue into the namespace is rejected. Reversible (operator hold). Existing in-flight jobs are unaffected.")]
    Suspended = 20,
}
