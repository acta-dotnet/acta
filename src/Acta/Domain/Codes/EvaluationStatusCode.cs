using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Why a schedule's next evaluation is when it is: the scheduling engine's state for one schedule, distinct from the
/// operator lifecycle (<see cref="ScheduleStatusCode"/>) and from the slot job's status.
/// </summary>
[JsonConverter(typeof(EvaluationStatusCodeJsonConverter))]
[CodeKind("schedule-evaluation-status")]
public enum EvaluationStatusCode : byte
{
    [Code("unspecified", "Evaluation status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("scheduled", "The next evaluation is the next nominal occurrence, or the end of a timed pause.")]
    Scheduled = 10,

    [Code(
        "waiting-for-capacity",
        "An occurrence is due but the permanent slot is busy or the schedule is at its outstanding limit; settlement wakes it."
    )]
    WaitingForCapacity = 20,

    [Code("exhausted", "The source offers no further occurrence; a declaration or calendar change reactivates it.")]
    Exhausted = 30,

    [Code("evaluation-error", "The source could not be evaluated; the cursor is kept and the evaluation retries after a bounded delay.")]
    EvaluationError = 40,

    [Code(
        "slot-unavailable",
        "An occurrence is due but the permanent slot is held, cancelled, or failed terminally; a job resume or restart wakes it."
    )]
    SlotUnavailable = 50,

    [Code(
        "resumed",
        "An operator resumed the schedule; its first evaluation decides the backlog under the misfire policy and expires the stale part without pausing again."
    )]
    Resumed = 60,
}
