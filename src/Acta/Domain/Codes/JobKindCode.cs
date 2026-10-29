using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What a <c>jobs</c> row is: an ordinary job, or the one permanent slot a scheduled definition fires through. The
/// kind is written at insert and never changes; the executor and the operator verbs read it instead of guessing from
/// a deduplication key or from the schedules a row happens to own.
/// </summary>
[JsonConverter(typeof(JobKindCodeJsonConverter))]
[CodeKind("job-kind")]
public enum JobKindCode : byte
{
    [Code("unspecified", "Job kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code(
        "ordinary",
        "A job enqueued by an application, an operator, or a parent handler, or admitted by a schedule as a per-occurrence root; it ends in a terminal status."
    )]
    Ordinary = 10,

    [Code(
        "permanent-slot",
        "The one reusable job of a scheduled definition; registration creates it, every firing of the definition's schedules runs through it, and it is never terminal by itself."
    )]
    PermanentSlot = 20,
}
