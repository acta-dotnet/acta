using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// A job's priority. A worker always claims the highest priority first, with no aging, so keep bulk work in a namespace of its own.
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

    [Code("realtime", "Highest priority.")]
    Realtime = 100,
}
