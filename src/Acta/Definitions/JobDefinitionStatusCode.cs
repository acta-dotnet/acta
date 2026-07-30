using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(JobDefinitionStatusCodeJsonConverter))]
[CodeKind("job-definition-status")]
public enum JobDefinitionStatusCode : byte
{
    // Numeric bands are a readability convention only; behavior matches explicit members.
    // Operators pause individual Jobs via IJobs.PauseAsync; there's no per-definition Deprecated
    // state by design.
    [Code("active", "Enqueue allowed; claim allowed.")]
    Active = 10,

    [Code(
        "retired",
        "Enqueue REJECTED; parked rows (Ready/Paused/Suspended) cancelled with ReasonCode = 'job.definition-retired'; in-flight executions finish their attempt."
    )]
    Retired = 240,
}
