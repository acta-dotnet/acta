namespace Acta;

/// <summary>
/// An operator's full override set for one schedule, guarded by the version they last read: a null
/// <c>Expression</c> or <c>TimeZoneId</c> clears that override.
/// </summary>
internal sealed record SetScheduleOverridesCommand(
    ScheduleLookup Schedule,
    int ExpectedVersion,
    string? Expression,
    string? TimeZoneId,
    string? ReasonMessage,
    string? Actor
);
