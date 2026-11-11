using System.Text.Json.Serialization;

namespace Acta;

/// <summary>How much of a job's history is recorded as events.</summary>
[JsonConverter(typeof(EventRecordingCodeJsonConverter))]
[CodeKind("event-recording")]
public enum EventRecordingCode : byte
{
    [Code("unspecified", "Event recording not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code(
        "failures",
        "Records every failed or orphaned attempt, a success that recovers from counted failures, a job freed from a stranded lane, a caller's pause, resume, restart, reschedule, priority change, input update, or cancel, and a schedule's pause, pause end, manual trigger, or failing evaluation. Under an enabled alert profile, also a one-shot's success past its first attempt, which resolves its alert."
    )]
    Failures = 10,

    [Code("audit", "Records every per-job event. The default for an application's jobs.")]
    Audit = 20,
}
