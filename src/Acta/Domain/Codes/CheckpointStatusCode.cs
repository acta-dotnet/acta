using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Where a checkpoint that has a status stands. A signal or child latch is pending until it is set or its wait expires; a timer is
/// pending until it is consumed; a firing is pending until it succeeds, fails, or is cancelled. Variables and progress have no status.
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
    [Code("expired", "The wait ran past its timeout and was settled as timed out; a raise after that skips it.")]
    Expired = 30,

    /// <summary>Due instant reached and the replayed handler consumed the timer.</summary>
    [Code("consumed", "The due time was reached; the handler ran again, consumed the timer, and proceeded.")]
    Consumed = 100,

    /// <summary>A firing envelope settled by a successful firing; the slot's last outcome until the next admission.</summary>
    [Code("succeeded", "The firing settled successfully; it stays the slot's last outcome until the next firing replaces it.")]
    Succeeded = 110,

    /// <summary>A firing envelope settled by a final failure.</summary>
    [Code("failed", "The firing settled as a final failure; it stays the slot's last outcome until the next firing replaces it.")]
    Failed = 120,

    /// <summary>A firing envelope settled by a cancellation.</summary>
    [Code("cancelled", "The firing settled by cancellation; it stays the slot's last outcome until the next firing replaces it.")]
    Cancelled = 130,
}
