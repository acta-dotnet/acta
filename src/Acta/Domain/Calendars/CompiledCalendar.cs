namespace Acta;

/// <summary>
/// A compiled catalog definition: its kind and role, the canonical JSON the content hash is taken over, and the
/// calendar it compiles to, one of the two by role.
/// </summary>
internal sealed record CompiledCalendar(
    string Kind,
    CalendarRoleCode Role,
    ReadOnlyMemory<byte> CanonicalJson,
    string ContentHash,
    IActaCalendar? Calendar,
    IActaAvailabilityCalendar? Availability
);
