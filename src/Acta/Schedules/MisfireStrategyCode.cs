using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(MisfireStrategyCodeJsonConverter))]
[CodeKind("misfire-strategy")]
public enum MisfireStrategyCode : byte
{
    [Code(
        "catch-up-once",
        "After downtime, fire once immediately for all missed occurrences, then resume from the next occurrence after now."
    )]
    CatchUpOnce = 10,

    [Code("skip", "After downtime, skip all missed occurrences and resume from the first occurrence strictly after now.")]
    Skip = 20,
}
