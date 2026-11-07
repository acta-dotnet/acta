using System.Text.Json.Serialization;

namespace Acta;

/// <summary>When the engine raises alerts for a definition's jobs on its own.</summary>
[JsonConverter(typeof(AlertProfileCodeJsonConverter))]
[CodeKind("alert-profile")]
public enum AlertProfileCode : byte
{
    [Code("none", "No automatic failure alerts. An alert a handler raises is still created.")]
    None = 0,

    [Code(
        "on-failure",
        "The default. Alerts on every failed or orphaned attempt; repeats collapse onto the job's one open incident, and the final failure raises a separate alert. Every enabled profile records the attempts its alerts read, whatever the event recording level. Resolves on recovery, the job's next success."
    )]
    OnFailure = 10,

    [Code(
        "info",
        "Informational alerts on a final failure only, at low severity. A permanent job rarely reaches one; see on-terminal. Resolves on recovery, the job's next success."
    )]
    Info = 20,

    [Code(
        "on-terminal",
        "Alerts on a final failure only: the retry budget is spent, the handler failed the job, an exception is not retryable, a step that runs at most once was interrupted, or a lost lease spent the budget. Resolves on recovery, the job's next success. A deadline cancels the job and so alerts under no profile. A permanent job never spends a budget, so use on-failure to hear about its failures."
    )]
    OnTerminal = 30,

    [Code(
        "sys-critical",
        "Reserved for Acta's own system jobs. Alerts at critical severity to the job's channel, or to the channel named default when the job declares none."
    )]
    SysCritical = 40,
}
