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
        "Records failed, orphaned, and finally failed attempts, a success only when it recovers from counted failures, a job that crash recovery releases from a stranded lane, and a caller's pause, resume, restart, reschedule, priority change, input update, or cancel. Under an enabled alert profile it also records a one-shot's success past its first attempt, which resolves the alert the failure opened."
    )]
    Failures = 10,

    [Code("audit", "Records every per-job event. The default for an application's jobs.")]
    Audit = 20,
}
