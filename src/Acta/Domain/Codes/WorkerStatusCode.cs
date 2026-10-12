using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Lifecycle status of a worker row. Mirrors the JobStatusCode band scheme: live states 10..90,
/// terminal success at 100, terminal error at 200+.
/// </summary>
[JsonConverter(typeof(WorkerStatusCodeJsonConverter))]
[CodeKind("worker-status")]
public enum WorkerStatusCode : byte
{
    [Code("unspecified", "Worker status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Polling claim; accepting work.")]
    Active = 10,

    [Code("draining", "Worker stopped polling; finishing in-flight.")]
    Draining = 80,

    [Code("stopped", "Worker shut down cleanly; terminal success.")]
    Stopped = 100,

    [Code("dead", "Heartbeat went stale; crash recovery reclaims lapsed leases, and a resumed heartbeat makes it Active again.")]
    Dead = 200,
}
