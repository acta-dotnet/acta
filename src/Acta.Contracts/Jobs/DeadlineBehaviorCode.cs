using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// How the engine treats a job that has passed its deadline.
/// </summary>
[JsonConverter(typeof(DeadlineBehaviorCodeJsonConverter))]
[CodeKind("job-deadline-behavior")]
public enum DeadlineBehaviorCode : byte
{
    /// <summary>
    /// The engine auto-terminates an overdue job: it refuses admission of an already-overdue claimed
    /// job and refuses to re-arm a retry whose next attempt would land past the deadline. The running
    /// handler is never force-cancelled by the deadline.
    /// </summary>
    [Code("strict", "Engine auto-terminates an overdue job at admission and refuses to re-arm a retry past the deadline.")]
    Strict = 10,

    /// <summary>
    /// The deadline is informational. The engine never auto-terminates; the job always runs and the
    /// handler decides what to do via ctx.IsOverdue.
    /// </summary>
    [Code("advisory", "Deadline is informational; the engine never auto-terminates, the handler reads ctx.IsOverdue.")]
    Advisory = 20,
}
