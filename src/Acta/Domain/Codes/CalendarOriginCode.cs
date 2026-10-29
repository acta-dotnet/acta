using System.Text.Json.Serialization;

namespace Acta;

/// <summary>Where a schedule's calendar comes from: the application's code or the namespace's calendar catalog.</summary>
[JsonConverter(typeof(CalendarOriginCodeJsonConverter))]
[CodeKind("calendar-origin")]
public enum CalendarOriginCode : byte
{
    [Code("unspecified", "Calendar origin not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>A calendar the application registers in code, bound by its name and exact version.</summary>
    [Code("registered", "A calendar registered by the application in code, by name and exact version.")]
    Registered = 10,

    /// <summary>A calendar published in the namespace's catalog; the published version binds the schedule.</summary>
    [Code("catalog", "A calendar published in the namespace's catalog; the published version binds the schedule.")]
    Catalog = 20,
}
