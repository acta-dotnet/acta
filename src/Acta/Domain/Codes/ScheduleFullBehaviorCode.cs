using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What a due occurrence does when its schedule already has its maximum of unsettled occurrences.</summary>
[JsonConverter(typeof(ScheduleFullBehaviorCodeJsonConverter))]
[CodeKind("schedule-full-behavior")]
public enum ScheduleFullBehaviorCode : byte
{
    [Code("unspecified", "Schedule full behavior not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>The occurrence stays undecided until a settlement frees capacity.</summary>
    [Code("wait", "Default. An occurrence of a full schedule stays undecided until a settlement frees capacity.")]
    Wait = 10,

    /// <summary>The occurrence is skipped, audited, and the schedule moves on.</summary>
    [Code("drop", "An occurrence of a full schedule is skipped, audited, and the schedule moves on.")]
    Drop = 20,
}
