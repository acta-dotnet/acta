using System.Text.Json.Serialization;

namespace Acta;

[JsonConverter(typeof(ScheduleExpressionKindCodeJsonConverter))]
[CodeKind("schedule-expression-kind")]
public enum ScheduleExpressionKindCode : byte
{
    [Code("cron", "Cron expression (Cronos dialect). Set at compile time by the source generator.")]
    Cron = 10,

    [Code(
        "interval",
        "Interval duration: human (e.g. \"5m\") or ISO 8601 (e.g. \"PT5M\", \"P1D\"). Set at compile time by the source generator."
    )]
    Interval = 20,
}
