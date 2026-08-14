using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertProfileCodeJsonConverter))]
[CodeKind("alert-profile")]
public enum AlertProfileCode : byte
{
    [Code("none", "No automatic failure alerts. ctx.AlertAsync still creates rows.")]
    None = 0,

    [Code(
        "on-failure",
        "Default. Alerts on every failure transition; dedupe-window collapses repeats. Final exhaustion writes a separate alert (a different JobEventReasonCode yields a different DeduplicationKey). Resolves on recovery."
    )]
    OnFailure = 10,

    [Code("info", "Informational alerts on final failure only; low severity.")]
    Info = 20,

    [Code("on-terminal", "Alert on terminal failure only (final exhaustion / orphan / deadline). Resolves on recovery.")]
    OnTerminal = 30,

    [Code(
        "sys-critical",
        "Reserved for system Jobs. Emits at Severity = Critical to the Job's channel, or the configured \"default\" channel when none is declared."
    )]
    SysCritical = 40,
}
