namespace Acta;

/// <summary>
/// Declares a code calendar a <c>[Schedule]</c> binds by name: <see cref="ScheduleAttribute.Calendar"/> binds an
/// <see cref="IActaCalendar"/> occurrence source, <see cref="ScheduleAttribute.Availability"/> an
/// <see cref="IActaAvailabilityCalendar"/>, and one class may implement both. Acta creates the class through dependency
/// injection at worker start, so its constructor may take registered services. The source generator rejects an
/// abstract, generic, or private class, one implementing neither interface, an invalid or <c>sys</c> name, and a name
/// two classes declare (<c>ACTA0128</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CalendarAttribute(string name) : Attribute
{
    /// <summary>
    /// Dotted kebab-case calendar name, at most 128 chars, unique in the compilation; <c>sys</c> names belong to Acta.
    /// </summary>
    public string Name { get; } = name;
}
