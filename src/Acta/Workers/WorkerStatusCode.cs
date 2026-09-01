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
    [Code("active", "Polling claim; accepting work.")]
    Active = 10,

    [Code("draining", "Worker stopped polling; finishing in-flight.")]
    Draining = 80,

    [Code("stopped", "Worker shut down cleanly; terminal success.")]
    Stopped = 100,

    [Code("dead", "Worker is dead; the sys.recovery system job reclaims leases.")]
    Dead = 200,
}
