using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// State machine for stateful <c>checkpoints</c> kinds. Signals and child latches move
/// <c>Pending</c> (awaited, no raise yet) to <c>Set</c> (raised, payload stored) or to
/// <c>Expired</c> (a re-entering bounded wait found the checkpoint overdue); timers move <c>Pending</c> (armed) to
/// <c>Consumed</c> (the replayed handler passed the due instant); the firing envelope moves <c>Pending</c>
/// (outstanding) to <c>Succeeded</c>, <c>Failed</c>, or <c>Cancelled</c> (settled, and the last outcome on record).
/// Stateless kinds (variable, progress) carry a NULL state.
/// </summary>
[JsonConverter(typeof(CheckpointStatusCodeJsonConverter))]
[CodeKind("job-checkpoint-status")]
public enum CheckpointStatusCode : byte
{
    [Code("unspecified", "Checkpoint status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>Awaited or armed; the checkpoint has not been satisfied yet.</summary>
    [Code("pending", "Awaited or armed; the checkpoint has not been satisfied yet.")]
    Pending = 10,

    /// <summary>Raised: the payload is stored and a waiting job proceeds (signals, child latches).</summary>
    [Code("set", "Raised; the payload is stored and a waiting job proceeds.")]
    Set = 20,

    /// <summary>
    /// Terminal for the checkpoint: the re-entering wait found it still Pending past its stored
    /// <c>due_at_utc</c> and settled it TimedOut there. Both a raise and a child landing terminal skip
    /// an Expired checkpoint, writing no payload and releasing no job, which is what stops a late arrival
    /// reviving a wait that already resolved.
    /// </summary>
    [Code("expired", "The re-entering wait found the checkpoint overdue and settled it TimedOut; a raise then skips it.")]
    Expired = 30,

    /// <summary>Due instant reached and the replayed handler consumed the timer.</summary>
    [Code("consumed", "Due instant reached; the replayed handler consumed the timer and proceeded.")]
    Consumed = 100,

    /// <summary>A firing envelope settled by a successful firing; the slot's last outcome until the next admission.</summary>
    [Code("succeeded", "The firing settled successfully; the envelope is the slot's last outcome until the next admission replaces it.")]
    Succeeded = 110,

    /// <summary>A firing envelope settled by a final failure.</summary>
    [Code("failed", "The firing settled as a final failure; the envelope is the slot's last outcome until the next admission replaces it.")]
    Failed = 120,

    /// <summary>A firing envelope settled by a cancellation.</summary>
    [Code("cancelled", "The firing settled by cancellation; the envelope is the slot's last outcome until the next admission replaces it.")]
    Cancelled = 130,
}
