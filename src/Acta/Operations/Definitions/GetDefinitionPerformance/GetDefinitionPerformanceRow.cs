namespace Acta;

/// <summary>
/// The executions of one interval, outcome event, and duration group or wait group as GetDefinitionPerformance counts
/// them, with when the oldest of them finished. A row names one group and leaves the other null; a row that names neither
/// counts the executions that recorded no wait.
/// </summary>
[DbProjection]
internal sealed record GetDefinitionPerformanceRow(
    int IntervalNumber,
    EventCode EventCode,
    int? DurationGroupMs,
    int? WaitGroupMs,
    long ExecutionCount,
    DateTime OldestAtUtc
);
