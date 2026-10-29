using System.Collections.Immutable;

namespace Acta;

/// <summary>
/// Generator-emitted descriptor for one declared <c>[Schedule]</c>. Source-time facts only;
/// next-occurrence instants are computed by the runtime, never here. For a calendar source <c>Expression</c> is the
/// calendar's name and <c>ExpressionKind</c> is <c>Calendar</c>; <c>AvailabilityCalendarName</c> names the availability
/// calendar, if any. A calendar is bound by name alone: registration resolves its origin and pins its version.
/// </summary>
public sealed record ScheduleDescriptor(
    string JobName,
    string ScheduleName,
    string Expression,
    string? TimeZoneId,
    MisfireStrategyCode MisfireStrategy,
    ScheduleExpressionKindCode ExpressionKind,
    string? Description,
    ImmutableArray<string> Environments,
    int MaxOutstanding = 1,
    ScheduleFullBehaviorCode FullBehavior = ScheduleFullBehaviorCode.Wait,
    int? ExpireAfterSeconds = null,
    ScheduleExpireActionCode ExpireAction = ScheduleExpireActionCode.Unspecified,
    string? AvailabilityCalendarName = null
);
