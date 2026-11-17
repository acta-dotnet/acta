using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a worker process is in its life.</summary>
[JsonConverter(typeof(WorkerStatusCodeJsonConverter))]
[CodeKind("worker-status")]
public enum WorkerStatusCode : byte
{
    [Code("unspecified", "Worker status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "Claiming and running jobs.")]
    Active = 10,

    [Code("draining", "Claiming no more jobs; finishing the ones it runs.")]
    Draining = 80,

    [Code("stopped", "Shut down cleanly.")]
    Stopped = 100,

    [Code(
        "dead",
        "Its heartbeat went stale; crash recovery reclaims its expired leases, and a heartbeat that resumes makes it active again."
    )]
    Dead = 200,
}
