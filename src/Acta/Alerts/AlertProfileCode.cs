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

    [Code("info", "Informational alerts on terminal failure only; low severity. A recurring slot rarely reaches one - see on-terminal.")]
    Info = 20,

    [Code(
        "on-terminal",
        "Alert on terminal failure only (retry budget exhausted, deadline, or a handler-declared failure). Resolves on recovery. A recurring slot never exhausts a budget and re-arms after an unhandled exception or a lost lease, so those never alert here; use on-failure to hear about them."
    )]
    OnTerminal = 30,

    [Code(
        "sys-critical",
        "Reserved for system Jobs. Emits at Severity = Critical to the Job's channel, or the configured \"default\" channel when none is declared."
    )]
    SysCritical = 40,
}
