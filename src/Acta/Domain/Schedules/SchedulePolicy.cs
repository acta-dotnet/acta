namespace Acta;

/// <summary>
/// What a schedule does with occurrences it finds already past: the misfire strategy and its grace, and the expiration
/// an occurrence may age past unadmitted before it is decided without work (<see cref="ExpireAfter"/> null: never),
/// with the action the expiry takes.
/// </summary>
internal sealed record SchedulePolicy(
    MisfireStrategyCode Misfire,
    TimeSpan Grace,
    TimeSpan? ExpireAfter,
    ScheduleExpireActionCode ExpireAction
);
