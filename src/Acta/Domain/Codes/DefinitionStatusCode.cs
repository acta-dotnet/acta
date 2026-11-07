using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a job definition is in its life.</summary>
[JsonConverter(typeof(DefinitionStatusCodeJsonConverter))]
[CodeKind("job-definition-status")]
public enum DefinitionStatusCode : byte
{
    [Code("unspecified", "Definition status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Enqueues are accepted and workers claim the definition's jobs.")]
    Active = 10,

    [Code(
        "retired",
        "Enqueues are rejected. Only an operator retires a definition; one that a deploy stops declaring stays active. Retiring cancels the definition's ready, paused, and suspended jobs with the event reason definition-retired; an executing job finishes its attempt, and one that would run again is cancelled with the same reason."
    )]
    Retired = 240,
}
