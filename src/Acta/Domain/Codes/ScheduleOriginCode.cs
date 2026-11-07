using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a schedule came from.</summary>
[JsonConverter(typeof(ScheduleOriginCodeJsonConverter))]
[CodeKind("schedule-origin")]
public enum ScheduleOriginCode : byte
{
    [Code("unspecified", "Schedule origin not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("operator", "Added by an operator. Nothing creates a schedule of this origin today.")]
    Operator = 20,

    [Code("definition", "Declared in the application's code, and refreshed when the application registers its definitions.")]
    Definition = 40,
}
