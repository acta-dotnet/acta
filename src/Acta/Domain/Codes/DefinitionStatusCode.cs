using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Lifecycle status of a job definition. Numeric bands are a readability convention only;
/// behavior follows the explicit members. There is no per-definition Deprecated state by design:
/// operators pause individual Jobs via <c>IJobs.PauseAsync</c>.
/// </summary>
[JsonConverter(typeof(DefinitionStatusCodeJsonConverter))]
[CodeKind("job-definition-status")]
public enum DefinitionStatusCode : byte
{
    [Code("unspecified", "Definition status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Enqueue allowed; claim allowed.")]
    Active = 10,

    [Code(
        "retired",
        "Enqueue REJECTED; an operator decision, never written by registration (a definition absent from a manifest stays Active). Retiring cancels parked rows (Ready/Paused/Suspended) with ReasonCode = 'job.definition-retired'; in-flight executions finish their attempt, and a job that would run again lands Cancelled with the same reason."
    )]
    Retired = 240,
}
