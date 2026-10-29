namespace Acta;

/// <summary>
/// A schedule's timing with the code it binds resolved: the occurrence calendar when the kind is Calendar, the
/// availability calendar when one is bound, each null when this process has not registered it. The calculator and
/// the evaluator take a source; <see cref="CalendarRegistry"/> builds one.
/// </summary>
internal sealed record ScheduleSource(ScheduleTiming Timing, IActaCalendar? Calendar, IActaAvailabilityCalendar? Availability)
{
    /// <summary>A cron or interval timing with no bindings, which needs no registry.</summary>
    public static ScheduleSource Native(string expression, string? timeZoneId, ScheduleExpressionKindCode kind) =>
        new(new ScheduleTiming(expression, kind, timeZoneId, null), null, null);

    /// <summary>This source, or the error naming the binding this process has not registered.</summary>
    public ScheduleSource Bound()
    {
        if (Timing.ExpressionKind == ScheduleExpressionKindCode.Calendar && Calendar is null)
        {
            throw new InvalidOperationException(Missing("calendar", Timing.Expression, Timing.CalendarOrigin, Timing.CalendarVersion));
        }

        if (Timing.AvailabilityCalendarName is { } name && Availability is null)
        {
            throw new InvalidOperationException(
                Missing("availability calendar", name, Timing.AvailabilityOrigin, Timing.AvailabilityVersion)
            );
        }

        return this;
    }

    private static string Missing(string role, string name, CalendarOriginCode? origin, int? version) =>
        origin switch
        {
            CalendarOriginCode.Registered =>
                $"The {role} '{name}' is not declared in this process; declare a [Calendar(\"{name}\")] class in a manifest this host runs or references, or register one on UseActa.",
            CalendarOriginCode.Catalog =>
                $"The {role} '{name}' version {version} is not in the namespace's calendar catalog or the shared one as a {role}.",
            _ => $"The {role} '{name}' has the origin {origin}, which this build does not bind.",
        };
}
