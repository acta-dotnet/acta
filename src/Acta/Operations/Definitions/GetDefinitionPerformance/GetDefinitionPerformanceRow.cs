namespace Acta;

/// <summary>
/// The executions of one interval, outcome event and duration group as GetDefinitionPerformance counts them, with when
/// the oldest of them finished.
/// </summary>
[DbProjection]
internal sealed record GetDefinitionPerformanceRow(
    int IntervalNumber,
    EventCode EventCode,
    int DurationGroupMs,
    long ExecutionCount,
    DateTime OldestAtUtc
);
