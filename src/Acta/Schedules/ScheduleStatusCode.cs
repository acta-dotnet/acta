using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ScheduleStatusCodeJsonConverter))]
[CodeKind("schedule-status")]
public enum ScheduleStatusCode : byte
{
    [Code("active", "Schedule is eligible to fire.")]
    Active = 10,

    [Code("paused", "Operator-paused; does not fire until resumed, or until PausedUntilUtc passes when set.")]
    Paused = 30,

    [Code("orphaned", "Origin declaration disappeared from the catalog; set by reconciliation. Cannot fire or be paused/resumed.")]
    Orphaned = 230,
}
