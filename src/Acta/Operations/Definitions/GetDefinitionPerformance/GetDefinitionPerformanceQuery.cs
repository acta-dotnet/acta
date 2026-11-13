namespace Acta;

/// <summary>
/// The definition and range <see cref="IDefinitions.GetPerformanceAsync"/> reads: <paramref name="FromUtc"/> inclusive,
/// <paramref name="ToUtc"/> exclusive and after it, both truncated to the millisecond, cut into intervals of
/// <paramref name="IntervalSeconds"/>, the last one shorter when the range does not divide evenly. An interval is at
/// least 60 seconds and a range holds at most 60 of them.
/// </summary>
public sealed record GetDefinitionPerformanceQuery(
    string JobNamespace,
    string JobName,
    DateTime FromUtc,
    DateTime ToUtc,
    int IntervalSeconds
);
