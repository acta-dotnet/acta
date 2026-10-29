using System.Text.Json.Serialization;

namespace Acta;

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

    [Code("skip", "After downtime, skip all missed occurrences and resume from the first occurrence strictly after now.")]
    Skip = 20,

    [Code(
        "catch-up-latest",
        "After downtime, fire once for the latest missed occurrence, the earlier ones coalesced into it, then resume from the next occurrence after now."
    )]
    CatchUpLatest = 30,

    [Code("catch-up-all", "After downtime, fire every missed occurrence in order, one per settlement, subject to the schedule's capacity.")]
    CatchUpAll = 40,
}
