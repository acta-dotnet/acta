using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(EventRecordingCodeJsonConverter))]
[CodeKind("event-recording")]
public enum EventRecordingCode : byte
{
    [Code(
        "off",
        "No recording-filtered per-job events; always-on system/catalog events still emit, and an enabled alert profile still records the failed, orphaned, terminally failed, and recovering attempts its alerts read."
    )]
    Off = 0,

    [Code(
        "failures",
        "Emit failed, orphaned, and terminally failed attempts, a success only when it recovers from counted failures, and lane repairs; suppress other recording-filtered per-job events."
    )]
    Failures = 10,

    [Code("audit", "Emit all recording-filtered per-job events. User Jobs default.")]
    Audit = 20,
}
