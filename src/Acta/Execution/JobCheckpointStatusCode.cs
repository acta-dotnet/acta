using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// State machine for stateful <c>JobCheckpoint</c> kinds. Signals and child latches move
/// <c>Pending</c> (awaited, no raise yet) to <c>Set</c> (raised, payload stored); timers move
/// <c>Pending</c> (armed) to <c>Consumed</c> (the replayed handler passed the due instant).
/// Stateless kinds (variable, progress) carry a NULL state.
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

    /// <summary>Due instant reached and the replayed handler consumed the timer.</summary>
    [Code("consumed", "Due instant reached; the replayed handler consumed the timer and proceeded.")]
    Consumed = 100,
}
