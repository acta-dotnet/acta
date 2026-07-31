namespace Acta;

/// <summary>
/// Declares a named recurring schedule on a <c>[Job]</c> handler. Multiple schedules may decorate
/// one handler; each becomes a <c>schedules</c> row under the definition's single recurring slot
/// <c>job</c>. The slot's hot-path cursor is the MIN of its live schedules' next occurrences, and
/// currently-due schedules coalesce into one execution whose due set the handler reads via
/// <see cref="JobContext.TriggeringScheduleNames"/>.
/// </summary>
/// <remarks>Declares a schedule.</remarks>
/// <param name="name">
/// Operator-stable kebab-case schedule name, unique within the definition.
/// </param>
/// <param name="expression">
/// Cron expression (Cronos dialect; 6 fields enables seconds) or an ISO 8601 duration (e.g.
/// <c>"PT5M"</c>). The generator infers the kind from the leading token (a <c>P</c> or <c>PT</c>
/// prefix means ISO).
/// </param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class JobScheduleAttribute(string name, string expression) : Attribute
{
    /// <summary>
    /// Operator-stable kebab-case schedule name, unique within the definition.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Cron expression or ISO 8601 duration. Stored verbatim as <c>schedules.expression</c>.
    /// </summary>
    public string Expression { get; } = expression;

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
    public string? TimeZone { get; init; } = "UTC";

    /// <summary>
    /// Environment names this schedule is active in (e.g. <c>"production"</c>). Empty or null means all
    /// environments.
    /// </summary>
    public string[]? Environments { get; init; }

    /// <summary>
    /// Behavior when occurrences were missed during downtime. Default
    /// <see cref="MisfireStrategyCode.Skip"/> (forward-only: drop missed occurrences and resume from
    /// the first occurrence after now). Set <see cref="MisfireStrategyCode.FireOnceCatchUp"/> to fire
    /// one coalesced catch-up occurrence on recovery.
    /// </summary>
    public MisfireStrategyCode Misfire { get; init; } = MisfireStrategyCode.Skip;
}
