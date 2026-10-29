using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted manifest of every <see cref="JobDescriptor"/> and <see cref="CalendarDescriptor"/> declared in an
/// <see cref="IJobManifest"/>-bearing assembly. Source-time facts only. A default <see cref="Calendars"/> reads as
/// empty.
/// </summary>
public sealed record JobDescriptorManifest(
    ImmutableArray<JobDescriptor> Descriptors,
    ImmutableArray<CalendarDescriptor> Calendars = default
);
