namespace Acta;

/// <summary>
/// Declares a named recurring schedule on a <c>[Job]</c> handler. Several may decorate one handler; each becomes a
/// <c>schedules</c> row with its own cursor under the definition's single permanent slot <c>job</c>. The admission
/// engine evaluates each schedule and admits one firing of the slot at a time; schedules due together contribute to
/// the same firing, which the handler reads via <see cref="JobContext.TriggeringSchedules"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class ScheduleAttribute : Attribute
{
    /// <summary>Declares a schedule whose occurrences come from an expression.</summary>
    /// <param name="name">Operator-stable kebab-case schedule name, unique within the definition.</param>
    /// <param name="expression">Cron (Cronos; 6 fields for seconds), an ISO 8601 duration ("PT5M"), or a plain one ("5m", "1h").</param>
    public ScheduleAttribute(string name, string expression)
    {
        Name = name;
        Expression = expression;
    }

    /// <summary>Declares a schedule whose occurrences come from the calendar named by <see cref="Calendar"/>.</summary>
    /// <param name="name">Operator-stable kebab-case schedule name, unique within the definition.</param>
    public ScheduleAttribute(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Operator-stable kebab-case schedule name, unique within the definition.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Cron expression or ISO 8601 duration. Stored verbatim as <c>schedules.expression</c>. Null for a schedule whose
    /// occurrences come from a <see cref="Calendar"/>.
    /// </summary>
    public string? Expression { get; }

    /// <summary>
    /// The name of the calendar whose occurrences this schedule fires, in place of an expression; dotted kebab-case.
    /// Declared with the name-only constructor; an expression and a calendar together are a build error. The name is
    /// found first among the calendars the process declares (<see cref="CalendarAttribute"/> classes and
    /// <c>AddCalendar</c> registrations), then in the namespace's calendar catalog, then in the shared catalog in
    /// <c>sys</c>.
    /// </summary>
    public string? Calendar { get; init; }

    /// <summary>
    /// The name of the availability calendar this schedule observes, whatever its occurrence source: an expression or a
    /// <see cref="Calendar"/>; dotted kebab-case. Null = none. The name is found the way <see cref="Calendar"/> is, with
    /// <c>AddAvailabilityCalendar</c> registrations in place of <c>AddCalendar</c> ones.
    /// </summary>
    public string? Availability { get; init; }

    /// <summary>
    /// Dev-authored explanation persisted on the schedule row's <c>description</c> column. Null = none.
    /// Distinct from the operator-written note (<c>ISchedules.PauseAsync</c>/<c>ResumeAsync</c>), which
    /// catalog re-sync never touches.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// IANA or Windows time-zone id resolved via <c>TimeZoneInfo.FindSystemTimeZoneById</c>. Defaults
    /// to UTC. Null or empty is also normalized to UTC. Ignored for ISO 8601 interval expressions.
    /// </summary>
    public string? TimeZoneId { get; init; } = "UTC";

    /// <summary>
    /// Environment names this schedule is active in (e.g. <c>"production"</c>). Empty or null means all
    /// environments.
    /// </summary>
    public string[]? Environments { get; init; }

    /// <summary>
    /// Behavior when occurrences were missed during downtime. Default
    /// <see cref="MisfireStrategyCode.Skip"/> (forward-only: drop missed occurrences and resume from
    /// the first occurrence after now). Set <see cref="MisfireStrategyCode.CatchUpOnce"/> to fire
    /// one coalesced catch-up occurrence on recovery.
    /// </summary>
    public MisfireStrategyCode MisfireStrategy { get; init; } = MisfireStrategyCode.Skip;

    /// <summary>
    /// How many admitted occurrences of this schedule may be unsettled at once; a Permanent definition's schedules
    /// allow exactly 1, the slot.
    /// </summary>
    public int MaxOutstanding { get; init; } = 1;

    /// <summary>
    /// What an occurrence does when the schedule is at <see cref="MaxOutstanding"/>: <c>Wait</c> keeps it undecided
    /// until a settlement frees capacity; <c>Drop</c> skips it, audited, and moves on.
    /// </summary>
    public ScheduleFullBehaviorCode FullBehavior { get; init; } = ScheduleFullBehaviorCode.Wait;

    /// <summary>
    /// The age an occurrence may reach past its instant before it expires unadmitted, in Acta duration syntax, e.g.
    /// <c>"1h"</c> or <c>"PT1H"</c>. Null = never. Set together with <see cref="ExpireAction"/>; one without the other is
    /// a build error.
    /// </summary>
    public string? ExpireAfter { get; init; }

    /// <summary>
    /// What an occurrence older than <see cref="ExpireAfter"/> does when it is evaluated: <c>Discard</c> skips it,
    /// audited, and moves on; <c>Pause</c> skips it, audited, and pauses the schedule until an operator resumes it.
    /// Default <c>Unspecified</c>, no expiry.
    /// </summary>
    public ScheduleExpireActionCode ExpireAction { get; init; }
}
