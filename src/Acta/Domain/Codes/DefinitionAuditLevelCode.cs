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
        "Emit job.execution-finished for a failure and for the success that answers one, but not for a one-off job's failed attempt that retries within budget, so its failures show only when terminal (a lost lease always shows); suppress other audit-filtered per-job events."
    )]
    Failures = 10,

    [Code("audit", "Emit all audit-filtered per-job events. User Jobs default.")]
    Audit = 20,
}
