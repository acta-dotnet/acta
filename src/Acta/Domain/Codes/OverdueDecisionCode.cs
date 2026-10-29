using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What a calendar publication does with the occurrences a dependent schedule has not decided yet that are already due
/// under the version it replaces.
/// </summary>
[JsonConverter(typeof(OverdueDecisionCodeJsonConverter))]
[CodeKind("overdue-decision")]
public enum OverdueDecisionCode : byte
{
    [Code("unspecified", "Overdue decision not recognized by this build; the value was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>A publication that would leave a dependent schedule's overdue occurrences undecided is rejected.</summary>
    [Code("refuse", "A publication with undecided overdue occurrences on a dependent schedule is rejected.")]
    Refuse = 10,

    /// <summary>The old stream's undecided prefix is audited as skipped, and the new stream starts after now.</summary>
    [Code("discard", "The old stream's undecided prefix is audited as skipped, and the new stream starts after now.")]
    Discard = 20,
}
