using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What a schedule does about occurrences it missed during downtime.</summary>
[JsonConverter(typeof(MisfireStrategyCodeJsonConverter))]
[CodeKind("misfire-strategy")]
public enum MisfireStrategyCode : byte
{
    [Code("unspecified", "Misfire strategy not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code(
        "catch-up-once",
        "After downtime, fire once immediately for all missed occurrences, then resume from the next occurrence after now."
    )]
    CatchUpOnce = 10,

    [Code("skip", "After downtime, skip the missed occurrences, except the latest when it is less than a minute overdue, which fires.")]
    Skip = 20,

    [Code(
        "catch-up-latest",
        "After downtime, fire once for the latest missed occurrence, however late, the earlier ones skipped, then resume from the next occurrence after now."
    )]
    CatchUpLatest = 30,

    [Code("catch-up-all", "After downtime, fire every missed occurrence in order, one per settlement, subject to the schedule's capacity.")]
    CatchUpAll = 40,
}
