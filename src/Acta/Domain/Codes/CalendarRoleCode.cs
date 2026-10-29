using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What a catalog calendar family gives a schedule: its occurrences, or the windows they may fire in. The family's first
/// version fixes its role, and every newer version keeps it.
/// </summary>
[JsonConverter(typeof(CalendarRoleCodeJsonConverter))]
[CodeKind("calendar-role")]
public enum CalendarRoleCode : byte
{
    [Code("unspecified", "Calendar role not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>A calendar whose instants are a schedule's occurrences.</summary>
    [Code("occurrence", "A calendar whose instants are a schedule's occurrences.")]
    Occurrence = 10,

    /// <summary>A calendar whose windows bound when a schedule's occurrences may fire.</summary>
    [Code("availability", "A calendar whose windows bound when a schedule's occurrences may fire.")]
    Availability = 20,
}
