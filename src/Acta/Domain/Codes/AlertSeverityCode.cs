using System.Text.Json.Serialization;

namespace Acta;

/// <summary>How severe an alert is.</summary>
[JsonConverter(typeof(AlertSeverityCodeJsonConverter))]
[CodeKind("alert-severity")]
public enum AlertSeverityCode : byte
{
    [Code("unspecified", "Alert severity not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("info", "Informational; not meant to page.")]
    Info = 10,

    [Code("warning", "An attempt failed and the job lives on; repeats collapse onto the one open incident.")]
    Warning = 20,

    [Code("error", "A final failure, or an event that needs an operator's attention.")]
    Error = 30,

    [Code(
        "critical",
        "The highest severity: raised by Acta's own system jobs, and by operators for incidents. Whether it pages depends on the channel it is routed to."
    )]
    Critical = 40,
}
