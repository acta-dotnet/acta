using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What a job is: an ordinary job, or the one permanent slot a scheduled definition fires through. The kind is set when the job is
/// created and never changes.
/// </summary>
/// <remarks>Code reads the kind instead of inferring it from a deduplication key or from the schedules a row owns.</remarks>
[JsonConverter(typeof(JobKindCodeJsonConverter))]
[CodeKind("job-kind")]
public enum JobKindCode : byte
{
    [Code("unspecified", "Job kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code(
        "ordinary",
        "A job enqueued by an application, an operator, or a parent job's handler, or created by a schedule for one occurrence; it ends in a final status."
    )]
    Ordinary = 10,

    [Code(
        "permanent",
        "The one reusable job of a scheduled definition: registering the definition creates it, every firing of the definition's schedules runs through it, and it never ends by itself."
    )]
    Permanent = 20,
}
