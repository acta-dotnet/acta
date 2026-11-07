using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What a checkpoint holds: one of the durable values a job keeps between attempts. Names are separate for each kind, so a variable never
/// collides with a signal or a child latch of the same name.
/// </summary>
/// <remarks>One table, <c>checkpoints</c>, holds every kind, keyed by <c>(job_id, checkpoint_kind_code, checkpoint_name)</c>.</remarks>
[JsonConverter(typeof(CheckpointKindCodeJsonConverter))]
[CodeKind("job-checkpoint-kind")]
public enum CheckpointKindCode : byte
{
    [Code("unspecified", "Checkpoint kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>User variable written by <c>SetVariableAsync</c> / <c>GetOrSetVariableAsync</c>.</summary>
    [Code("variable", "A variable a handler sets; the last write wins.")]
    Variable = 10,

    /// <summary>Signal raised by <c>RaiseSignalAsync</c> and awaited by <c>ctx.WaitSignalAsync</c>.</summary>
    [Code("signal", "A signal; pending while awaited, set once raised.")]
    Signal = 20,

    /// <summary>Durable sleep timer armed by <c>ctx.SleepAsync</c>.</summary>
    [Code("timer", "A durable sleep timer; pending until due, consumed when the handler runs again past the due time.")]
    Timer = 30,

    /// <summary>The job's single progress checkpoint, written by <c>ctx.SetProgressAsync</c>.</summary>
    [Code("progress", "The job's progress, one per job; the last write wins.")]
    Progress = 40,

    /// <summary>Child terminal-outcome latch raised on the parent when a child lands terminal.</summary>
    [Code("child-latch", "The latch a parent job holds for one child's outcome; pending while awaited, set when the child ends.")]
    ChildLatch = 50,

    /// <summary>
    /// The engine's firing envelope, one <c>sys.firing</c> checkpoint per scheduled job: the contributing schedules and
    /// occurrences an admission froze, Pending while the firing is outstanding and settled with its outcome. Owned by
    /// the scheduler, never by the handler: <c>ResetJobState</c> leaves it alone and the checkpoint read omits it.
    /// </summary>
    [Code(
        "firing",
        "The record of one scheduled firing of a permanent job or of a per-occurrence job; pending while outstanding, then settled with the outcome. It survives a handler's state reset."
    )]
    Firing = 60,
}
