namespace Acta;

/// <summary>
/// Ready-made cron expressions for <see cref="JobScheduleAttribute"/>. Cronos dialect: five fields
/// (<c>minute hour day-of-month month day-of-week</c>), with a six-field <c>second ...</c> variant for
/// sub-minute cadence. Day-of-week is <c>0 = Sunday</c> through <c>6 = Saturday</c>. Clock-time
/// entries fire in the schedule's <see cref="JobScheduleAttribute.TimeZone"/> (default UTC).
/// Interval entries (<c>*/N</c>) align to the clock, not to schedule registration:
/// <see cref="Every5Minutes"/> fires at minutes 0, 5, 10, ... of each hour.
/// </summary>
public static class Cron
{
    // Sub-minute (six-field, seconds-leading)

    /// <summary>Every 15 seconds.</summary>
    public const string Every15Seconds = "*/15 * * * * *";

    /// <summary>Every 30 seconds.</summary>
    public const string Every30Seconds = "*/30 * * * * *";

    // Minute

    /// <summary>Every minute, on the minute.</summary>
    public const string EveryMinute = "* * * * *";

    /// <summary>Every 2 minutes.</summary>
    public const string Every2Minutes = "*/2 * * * *";

    /// <summary>Every 3 minutes.</summary>
    public const string Every3Minutes = "*/3 * * * *";

    /// <summary>Every 5 minutes.</summary>
    public const string Every5Minutes = "*/5 * * * *";

    /// <summary>Every 10 minutes.</summary>
    public const string Every10Minutes = "*/10 * * * *";

    /// <summary>Every 15 minutes.</summary>
    public const string Every15Minutes = "*/15 * * * *";

    /// <summary>Every 30 minutes.</summary>
    public const string Every30Minutes = "*/30 * * * *";

    // Hour

    /// <summary>Every hour, on the hour.</summary>
    public const string Hourly = "0 * * * *";

    /// <summary>Every 2 hours, on the hour.</summary>
    public const string Every2Hours = "0 */2 * * *";

    /// <summary>Every 4 hours, on the hour.</summary>
    public const string Every4Hours = "0 */4 * * *";

    /// <summary>Every 6 hours, on the hour.</summary>
    public const string Every6Hours = "0 */6 * * *";

    /// <summary>Every 8 hours, on the hour.</summary>
    public const string Every8Hours = "0 */8 * * *";

    /// <summary>Every 12 hours, on the hour.</summary>
    public const string Every12Hours = "0 */12 * * *";

    // Day (24-hour clock, in the schedule's time zone)

    /// <summary>Every day at midnight.</summary>
    public const string Daily = "0 0 * * *";

    /// <summary>Every day at 05:00.</summary>
    public const string DailyAt5 = "0 5 * * *";

    /// <summary>Every day at 09:00.</summary>
    public const string DailyAt9 = "0 9 * * *";

    /// <summary>Every day at noon.</summary>
    public const string DailyAtNoon = "0 12 * * *";

    /// <summary>Every day at 13:00.</summary>
    public const string DailyAt13 = "0 13 * * *";

    /// <summary>Every day at 18:00.</summary>
    public const string DailyAt18 = "0 18 * * *";

    // Week (0 = Sunday through 6 = Saturday)

    /// <summary>Weekly, Sunday at midnight (alias of <see cref="EverySunday"/>).</summary>
    public const string Weekly = "0 0 * * 0";

    /// <summary>Every Sunday at midnight.</summary>
    public const string EverySunday = "0 0 * * 0";

    /// <summary>Every Monday at midnight.</summary>
    public const string EveryMonday = "0 0 * * 1";

    /// <summary>Every Tuesday at midnight.</summary>
    public const string EveryTuesday = "0 0 * * 2";

    /// <summary>Every Wednesday at midnight.</summary>
    public const string EveryWednesday = "0 0 * * 3";

    /// <summary>Every Thursday at midnight.</summary>
    public const string EveryThursday = "0 0 * * 4";

    /// <summary>Every Friday at midnight.</summary>
    public const string EveryFriday = "0 0 * * 5";

    /// <summary>Every Saturday at midnight.</summary>
    public const string EverySaturday = "0 0 * * 6";

    /// <summary>Every Monday at 08:00.</summary>
    public const string EveryMondayAt8 = "0 8 * * 1";

    /// <summary>Midnight Monday through Friday.</summary>
    public const string Weekdays = "0 0 * * 1-5";

    /// <summary>Midnight Saturday and Sunday.</summary>
    public const string Weekends = "0 0 * * 0,6";

    /// <summary>On the hour from 09:00 to 17:00, Monday through Friday (fires once per hour).</summary>
    public const string BusinessHours = "0 9-17 * * 1-5";

    // Month

    /// <summary>Monthly, the 1st at midnight (alias of <see cref="FirstOfMonth"/>).</summary>
    public const string Monthly = "0 0 1 * *";

    /// <summary>The 1st of each month at midnight.</summary>
    public const string FirstOfMonth = "0 0 1 * *";

    /// <summary>The 1st of each month at 05:00.</summary>
    public const string FirstOfMonthAt5 = "0 5 1 * *";

    /// <summary>The 15th of each month at midnight.</summary>
    public const string FifteenthOfMonth = "0 0 15 * *";

    // Quarter (January, April, July, October)

    /// <summary>The 1st of each quarter at midnight (alias of <see cref="FirstOfQuarter"/>).</summary>
    public const string Quarterly = "0 0 1 */3 *";

    /// <summary>The 1st of each quarter at midnight.</summary>
    public const string FirstOfQuarter = "0 0 1 */3 *";

    /// <summary>The 15th of each quarter at midnight.</summary>
    public const string FifteenthOfQuarter = "0 0 15 */3 *";

    // Year

    /// <summary>January 1st at midnight.</summary>
    public const string Yearly = "0 0 1 1 *";
}
