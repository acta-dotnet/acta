using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ScheduleOriginCodeJsonConverter))]
[CodeKind("schedule-origin")]
public enum ScheduleOriginCode : byte
{
    [Code("operator", "Operator-driven schedule add/remove. No code path produces rows with this origin today.")]
    Operator = 20,

    [Code("definition", "Declared in [JobSchedule] attribute; refreshed by catalog upsert.")]
    Definition = 40,
}
