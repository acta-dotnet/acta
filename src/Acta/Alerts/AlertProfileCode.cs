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
        "Default. Alerts on every failure transition; repeats collapse onto the job's one open incident. Final exhaustion writes a separate alert (a different JobEventReasonCode yields a different DeduplicationKey). Resolves on recovery."
    )]
    OnFailure = 10,

    [Code("info", "Informational alerts on terminal failure only; low severity. A recurring slot rarely reaches one - see on-terminal.")]
    Info = 20,

    [Code(
        "on-terminal",
        "Alert on terminal failure only: retry budget exhausted, ctx.FailAsync, a non-retryable exception, or an interrupted at-most-once step. Resolves on recovery. A deadline lands the job Cancelled rather than Failed and so alerts under no profile. A recurring slot never exhausts a budget and re-arms after an unhandled exception or a lost lease, so use on-failure to hear about those."
    )]
    OnTerminal = 30,

    [Code(
        "sys-critical",
        "Reserved for system Jobs. Emits at Severity = Critical to the Job's channel, or the configured \"default\" channel when none is declared."
    )]
    SysCritical = 40,
}
