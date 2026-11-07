using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a job is in its life.</summary>
[JsonConverter(typeof(JobStatusCodeJsonConverter))]
[CodeKind("job-status")]
public enum JobStatusCode : byte
{
    [Code("unspecified", "Job status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("ready", "Waiting for a worker to claim it.")]
    Ready = 10,

    [Code(
        "blocked",
        "Waiting behind an earlier unfinished job of its lane; no worker is offered it, and it becomes ready when the job ahead ends."
    )]
    Blocked = 15,

    [Code(
        "suspended",
        "Stopped until something outside the attempt happens: a signal its handler waits for, or the end of child jobs it waits on."
    )]
    Suspended = 20,

    [Code("paused", "Not running until it is resumed.")]
    Paused = 30,

    [Code(
        "idle",
        "A permanent slot between firings, with nothing outstanding and no lease. A firing makes it ready; it never ends by itself."
    )]
    Idle = 40,

    [Code("executing", "Claimed by a worker under a live lease; its handler is running or about to start.")]
    Executing = 50,

    [Code("succeeded", "Ended successfully.")]
    Succeeded = 100,

    [Code(
        "failed",
        "Ended in failure: its attempts are spent, an exception is not retryable, a step that runs at most once was interrupted, or its handler failed it."
    )]
    Failed = 200,

    [Code(
        "cancelled",
        "Ended by cancellation: by an operator, a caller, or its own handler, by a strict deadline, or because its definition was retired."
    )]
    Cancelled = 220,
}
