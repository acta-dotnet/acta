using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(AlertSeverityCodeJsonConverter))]
[CodeKind("alert-severity")]
public enum AlertSeverityCode : byte
{
    [Code("info", "Informational; not paging.")]
    Info = 10,

    [Code("warning", "Non-terminal failure transition; repeats collapse onto the one open incident.")]
    Warning = 20,

    [Code("error", "Terminal failure or operator-attention event.")]
    Error = 30,

    [Code(
        "critical",
        "Highest severity; emitted by system Jobs (AlertProfile = SysCritical) and operator alerts for incidents. Whether it pages depends on the routed channel's transport and config."
    )]
    Critical = 40,
}
