using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// State machine for stateful <c>JobCheckpoint</c> kinds. Signals and child latches move
/// <c>Pending</c> (awaited, no raise yet) to <c>Set</c> (raised, payload stored) or to
/// <c>Expired</c> (a re-entering bounded wait found the slot overdue); timers move <c>Pending</c> (armed) to
/// <c>Consumed</c> (the replayed handler passed the due instant). Stateless kinds (variable,
/// progress) carry a NULL state.
/// </summary>
[JsonConverter(typeof(JobCheckpointStatusCodeJsonConverter))]
[CodeKind("job-checkpoint-status")]
public enum JobCheckpointStatusCode : byte
{
    /// <summary>Awaited or armed; the slot has not been satisfied yet.</summary>
    [Code("pending", "Awaited or armed; the slot has not been satisfied yet.")]
    Pending = 10,

    /// <summary>Raised: the payload is stored and a waiting job proceeds (signals, child latches).</summary>
    [Code("set", "Raised; the payload is stored and a waiting job proceeds.")]
    Set = 20,

    /// <summary>
    /// Terminal for the slot: the re-entering wait found the slot still Pending past its stored
    /// <c>due_at_utc</c> and settled it TimedOut there. Both a raise and a child landing terminal skip
    /// an Expired slot, writing no payload and releasing no job, which is what stops a late arrival
    /// reviving a wait that already resolved.
    /// </summary>
    [Code("expired", "The re-entering wait found the slot overdue and settled it TimedOut; a raise then skips the slot.")]
    Expired = 30,

    /// <summary>Due instant reached and the replayed handler consumed the timer.</summary>
    [Code("consumed", "Due instant reached; the replayed handler consumed the timer and proceeded.")]
    Consumed = 100,
}
