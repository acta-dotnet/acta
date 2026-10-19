using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted descriptor for one declared <c>[JobSchedule]</c>. Source-time facts only;
/// next-occurrence instants are computed by the runtime, never here.
/// </summary>
public sealed record ScheduleDescriptor(
    string JobName,
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    ImmutableArray<string> Environments
);
