using System.Text.Json.Serialization;

namespace Acta;

/// <summary>What kind of expression a schedule has.</summary>
[JsonConverter(typeof(ScheduleExpressionKindCodeJsonConverter))]
[CodeKind("schedule-expression-kind")]
public enum ScheduleExpressionKindCode : byte
{
    [Code("unspecified", "Schedule expression kind not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    [Code("cron", "A cron expression.")]
    Cron = 10,

    [Code("interval", "An interval, in short form such as 5m or in ISO 8601 such as PT5M or P1D.")]
    Interval = 20,

    [Code("calendar", "A calendar the expression names; the schedule's occurrences come from that calendar.")]
    Calendar = 30,
}
