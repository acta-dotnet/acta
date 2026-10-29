using System.Diagnostics.CodeAnalysis;

namespace Acta;

/// <summary>
/// Generator-emitted descriptor for one <c>[Calendar]</c> class. <see cref="Occurrence"/> is true when the class
/// implements <see cref="IActaCalendar"/>, <see cref="Availability"/> when it implements
/// <see cref="IActaAvailabilityCalendar"/>; at least one is true.
/// </summary>
public sealed record CalendarDescriptor(
    string CalendarName,
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type ImplementationType,
    bool Occurrence,
    bool Availability
);
