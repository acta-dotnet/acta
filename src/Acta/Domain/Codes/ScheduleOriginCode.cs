using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ScheduleOriginCodeJsonConverter))]
[CodeKind("schedule-origin")]
public enum ScheduleOriginCode : byte
{
    [Code("unspecified", "Schedule origin not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("operator", "Operator-driven schedule add/remove. No code path produces rows with this origin today.")]
    Operator = 20,

    [Code("definition", "Declared in [JobSchedule] attribute; refreshed by catalog upsert.")]
    Definition = 40,
}
