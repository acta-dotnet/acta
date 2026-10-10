using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(DefinitionAuditLevelCodeJsonConverter))]
[CodeKind("job-audit-level")]
public enum DefinitionAuditLevelCode : byte
{
    [Code("off", "No audit-filtered per-job events; always-on system/catalog events still emit.")]
    Off = 0,

    [Code(
        "failures",
        "Emit job.execution-finished for a failed attempt and for the success that answers one; suppress other audit-filtered per-job events."
    )]
    Failures = 10,

    [Code("audit", "Emit all audit-filtered per-job events. User Jobs default.")]
    Audit = 20,
}
