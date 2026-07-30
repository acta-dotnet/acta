using System.Collections.Immutable;
using Acta.Configuration;

namespace Acta.Features.Schedules;

/// <summary>
/// Environment gating for declared schedules. A <c>[JobSchedule]</c> whose <c>Environments</c> is empty
/// is a wildcard active in every environment; otherwise it registers only when the worker's current
/// environment (<see cref="JobsOptions.EnvironmentName"/>) matches one of its declared names,
/// case-insensitively, mirroring .NET host-environment comparison. A scoped schedule is inactive when
/// the current environment is unknown (null or empty), so an environment-scoped schedule never fires on
/// a host that has not declared which environment it is.
/// </summary>
internal static class ScheduleEnvironment
{
    public static bool IsActiveIn(ImmutableArray<string> environments, string? currentEnvironment) =>
        environments.IsDefaultOrEmpty || environments.Contains(currentEnvironment ?? string.Empty, StringComparer.OrdinalIgnoreCase);
}
