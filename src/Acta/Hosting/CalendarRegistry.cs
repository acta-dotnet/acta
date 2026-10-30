using Microsoft.Extensions.DependencyInjection;

namespace Acta;

/// <summary>
/// The calendars this process holds in code, by name: the <c>[Calendar]</c> classes of every manifest the host runs or
/// references, created through DI when the registry is first needed, and the instances the builder registered with
/// <c>AddCalendar</c>. A schedule's calendar name resolves here first, at every evaluation, before the catalog. One name
/// is one implementation, under each role it implements; a second registration of a name with another instance throws.
/// </summary>
internal sealed class CalendarRegistry
{
    /// <summary>A process with no calendars, which every cron or interval plan can use.</summary>
    public static CalendarRegistry Empty { get; } = new();

    private readonly Dictionary<string, IActaCalendar> _calendars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IActaAvailabilityCalendar> _availability = new(StringComparer.Ordinal);

    public void Add(string name, IActaCalendar implementation) => Register(_calendars, "calendar", name, implementation);

    public void AddAvailability(string name, IActaAvailabilityCalendar implementation) =>
        Register(_availability, "availability calendar", name, implementation);

    /// <summary>Creates each declared calendar through <paramref name="services"/> and registers it under the roles its class implements.</summary>
    public CalendarRegistry AddDeclared(IServiceProvider services, IEnumerable<CalendarDescriptor> declared)
    {
        foreach (var d in declared)
        {
            var instance = ActivatorUtilities.CreateInstance(services, d.ImplementationType);
            if (instance is IActaCalendar calendar)
            {
                Add(d.CalendarName, calendar);
            }

            if (instance is IActaAvailabilityCalendar availability)
            {
                AddAvailability(d.CalendarName, availability);
            }
        }

        return this;
    }

    /// <summary>Whether this process declares <paramref name="name"/> under <paramref name="role"/>; such a name never reads the catalog.</summary>
    public bool Declares(string name, CalendarRoleCode role) =>
        role == CalendarRoleCode.Occurrence ? _calendars.ContainsKey(name) : _availability.ContainsKey(name);

    /// <summary>The source of <paramref name="timing"/> from the code calendars; a name this process lacks is left null for the catalog.</summary>
    public ScheduleSource Source(ScheduleTiming timing) =>
        new(
            timing,
            timing.ExpressionKind == ScheduleExpressionKindCode.Calendar ? _calendars.GetValueOrDefault(timing.Expression) : null,
            timing.AvailabilityCalendarName is { } name ? _availability.GetValueOrDefault(name) : null
        );

    private static void Register<T>(Dictionary<string, T> registry, string role, string name, T implementation)
        where T : class
    {
        var canonical = NameSyntax.Canonicalize(name, nameof(name), NameSyntax.ExtendedMaxLength, reserveSystem: true);
        ArgumentNullException.ThrowIfNull(implementation);
        if (registry.TryGetValue(canonical, out var existing) && !ReferenceEquals(existing, implementation))
        {
            throw new InvalidOperationException(
                $"The {role} '{canonical}' is declared twice with different implementations; a name is one calendar."
            );
        }

        registry[canonical] = implementation;
    }
}
