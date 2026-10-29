using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// Where one version of a catalog calendar family stands. At most one version of a family is Published at a time, and
/// that version is the family's current one; a version's content never changes, only its status.
/// </summary>
[JsonConverter(typeof(CalendarStatusCodeJsonConverter))]
[CodeKind("calendar-status")]
public enum CalendarStatusCode : byte
{
    [Code("unspecified", "Calendar status not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>A saved version that was never published; no schedule follows it.</summary>
    [Code("draft", "A saved version that was never published; no schedule follows it.")]
    Draft = 10,

    /// <summary>The family's current version, the one its catalog-bound schedules follow.</summary>
    [Code("published", "The family's current version, the one its catalog-bound schedules follow.")]
    Published = 20,

    /// <summary>A version that was current until a newer one was published; history still pins it.</summary>
    [Code("superseded", "A version that was current until a newer one was published; history still pins it.")]
    Superseded = 30,

    /// <summary>The family's current version, retired: the family has no current version until another is published.</summary>
    [Code("archived", "The family's current version, retired; the family has no current version until another is published.")]
    Archived = 240,
}
