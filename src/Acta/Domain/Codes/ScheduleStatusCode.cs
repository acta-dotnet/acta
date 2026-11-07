using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a schedule is in its life.</summary>
[JsonConverter(typeof(ScheduleStatusCodeJsonConverter))]
[CodeKind("schedule-status")]
public enum ScheduleStatusCode : byte
{
    [Code("unspecified", "Schedule status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("active", "The schedule fires.")]
    Active = 10,

    [Code("paused", "Paused by an operator; it does not fire until it is resumed, or until pausedUntilUtc passes when that is set.")]
    Paused = 30,

    [Code("orphaned", "The application's code no longer declares the schedule; it cannot fire, be paused, or be resumed.")]
    Orphaned = 230,
}
