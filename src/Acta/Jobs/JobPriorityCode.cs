using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Claim-priority value for <c>JobRuntime.Priority</c>. Discrete five-tier code-system enum
/// (<c>Bulk</c> / <c>Normal</c> / <c>High</c> / <c>Critical</c> / <c>Realtime</c>); no intermediate
/// values. Strict priority ordering on claim (no aging, no anti-starvation budget), so operators
/// isolate bulk workloads in their own <c>JobNamespace</c>. Set by the definition policy, definition
/// override, or per-enqueue priority override.
/// </summary>
[JsonConverter(typeof(JobPriorityCodeJsonConverter))]
[CodeKind("job-priority")]
public enum JobPriorityCode : byte
{
    [Code("bulk", "Lowest practical priority; bulk-class workloads.")]
    Bulk = 0,

    [Code("normal", "Default priority. Most jobs use this.")]
    Normal = 50,

    [Code("high", "Above normal; operator opt-in for time-sensitive work.")]
    High = 70,

    [Code("critical", "Critical priority; close-to-realtime operator workloads.")]
    Critical = 85,

    [Code("realtime", "Highest priority; always pages.")]
    Realtime = 100,
}
