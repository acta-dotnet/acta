using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Why a schedule's next evaluation is when it is: where the scheduling engine stands with one schedule, apart from the schedule's own
/// status and from the status of the job it fires.
/// </summary>
[JsonConverter(typeof(ScheduleEvaluationStatusCodeJsonConverter))]
[CodeKind("schedule-evaluation-status")]
public enum ScheduleEvaluationStatusCode : byte
{
    [Code("unspecified", "Evaluation status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("scheduled", "The next evaluation is the next nominal occurrence, or the end of a timed pause.")]
    Scheduled = 10,

    [Code(
        "waiting-for-capacity",
        "An occurrence is due but the permanent job is busy or the schedule is at its limit of outstanding occurrences; it fires when one of them ends."
    )]
    WaitingForCapacity = 20,

    [Code("exhausted", "The schedule has no further occurrence; a change to its declaration or to its calendar reactivates it.")]
    Exhausted = 30,

    [Code(
        "evaluation-error",
        "The schedule's expression or calendar could not be evaluated; the schedule keeps its place and the evaluation retries after a bounded delay."
    )]
    EvaluationError = 40,

    [Code(
        "job-unavailable",
        "An occurrence is due but the permanent job is held, cancelled, or failed terminally; a job resume or restart wakes it."
    )]
    JobUnavailable = 50,

    [Code(
        "resumed",
        "An operator resumed the schedule; its first evaluation decides the backlog under the misfire policy and expires the stale part without pausing again."
    )]
    Resumed = 60,
}
