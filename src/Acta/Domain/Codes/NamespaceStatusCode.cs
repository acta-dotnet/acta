using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Whether a namespace accepts enqueues.</summary>
/// <remarks>Ids are spaced by 10, with live states in 10..80, as in <see cref="TenantStatusCode"/>.</remarks>
[JsonConverter(typeof(NamespaceStatusCodeJsonConverter))]
[CodeKind("namespace-status")]
public enum NamespaceStatusCode : byte
{
    [Code("unspecified", "Namespace status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Enqueues into the namespace are accepted.")]
    Active = 10,

    [Code(
        "suspended",
        "Enqueues into the namespace are rejected, a running job's child start included, and its schedules hold, the system jobs' too, until an operator resumes it. Jobs it already holds still run."
    )]
    Suspended = 20,
}
