using System.Globalization;

namespace Acta;

/// <summary>What the calendar verbs share: a lookup's names in their stored form, and their events' details.</summary>
internal static class CalendarControl
{
    /// <summary>The lookup's namespace and calendar name canonicalized; a bad or reserved name throws <see cref="ArgumentException"/>.</summary>
    public static (string JobNamespace, string CalendarName) Names(CalendarLookup calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return (
            NameSyntax.Canonicalize(calendar.JobNamespace, nameof(calendar.JobNamespace)),
            NameSyntax.Canonicalize(calendar.CalendarName, nameof(calendar.CalendarName), NameSyntax.ExtendedMaxLength, reserveSystem: true)
        );
    }

    /// <summary>A calendar event's details: the calendar by name, the version, the role when the event records one, and the operator.</summary>
    public static byte[]? Details(string calendarName, int version, CalendarRoleCode? role, string? actor) =>
        EventDetails.Write(
            ("calendar", calendarName),
            ("version", version.ToString(CultureInfo.InvariantCulture)),
            ("role", role is { } r ? r.Code : null),
            ("actor", JobControlActor.Operator(actor))
        );
}
