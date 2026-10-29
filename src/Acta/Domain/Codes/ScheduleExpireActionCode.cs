using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What happens to an occurrence that reaches its schedule's expiry before it is admitted.</summary>
[JsonConverter(typeof(ScheduleExpireActionCodeJsonConverter))]
[CodeKind("schedule-expire-action")]
public enum ScheduleExpireActionCode : byte
{
    [Code("unspecified", "Schedule expire action not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>The expired occurrence is skipped, audited, and the schedule moves on.</summary>
    [Code(
        "discard",
        "An occurrence older than the schedule's expiry when it is evaluated is skipped, audited with the reason expired, and the schedule moves on."
    )]
    Discard = 10,

    /// <summary>The expired occurrence is skipped, audited, and the schedule pauses until an operator resumes it.</summary>
    [Code(
        "pause",
        "An occurrence older than the schedule's expiry when it is evaluated is skipped, audited with the reason expired, and the schedule is paused with the reason until an operator resumes it."
    )]
    Pause = 20,
}
