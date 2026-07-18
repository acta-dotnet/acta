using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Discriminator for <c>JobCheckpoint</c> rows: which durable substrate feature owns the
/// <c>(job_id, kind_code, name)</c> slot. One physical table carries every simple job-internal
/// durable slot; the kind keeps the name spaces structurally separate (a user variable can never
/// collide with a signal or a child latch of the same name).
/// </summary>
[JsonConverter(typeof(JobCheckpointKindCodeJsonConverter))]
[CodeKind("job-checkpoint-kind")]
public enum JobCheckpointKindCode : byte
{
    /// <summary>User variable slot written by <c>SetVariableAsync</c> / <c>GetOrSetVariableAsync</c>.</summary>
    [Code("variable", "User variable slot; UPSERT, last-writer-wins, no state machine.")]
    Variable = 10,

    /// <summary>Signal slot raised by <c>RaiseSignalAsync</c> and awaited by <c>ctx.WaitSignalAsync</c>.</summary>
    [Code("signal", "Signal slot; Pending while awaited, Set once raised.")]
    Signal = 20,

    /// <summary>Durable sleep timer armed by <c>ctx.SleepAsync</c>.</summary>
    [Code("timer", "Durable sleep timer; Pending until due, Consumed on replay past the due instant.")]
    Timer = 30,

    /// <summary>The Job's single progress slot written by <c>ctx.SetProgressAsync</c>.</summary>
    [Code("progress", "Progress slot; UPSERT, last-writer-wins, one per job.")]
    Progress = 40,

    /// <summary>Child terminal-outcome latch raised on the parent when a child lands terminal.</summary>
    [Code("child-latch", "Child terminal-outcome latch on the parent; Pending while awaited, Set when the child lands terminal.")]
    ChildLatch = 50,
}
