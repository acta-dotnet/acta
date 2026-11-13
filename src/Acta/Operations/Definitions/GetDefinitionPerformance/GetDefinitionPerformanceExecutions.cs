namespace Acta;

/// <summary>
/// The executions of one outcome in a range or an interval: how many, and their duration percentiles. A percentile is
/// truncated to two significant figures, so 2099 ms reads 2000.
/// </summary>
/// <param name="ExecutionCount">How many executions were counted.</param>
/// <param name="P50Ms">The duration half of the executions finished within, in milliseconds; null when none was counted.</param>
/// <param name="P95Ms">The duration 95 in 100 executions finished within, in milliseconds; null when none was counted.</param>
/// <param name="P99Ms">The duration 99 in 100 executions finished within, in milliseconds; with fewer than 100 it is the slowest one. Null when none was counted.</param>
public sealed record GetDefinitionPerformanceExecutions(long ExecutionCount, int? P50Ms, int? P95Ms, int? P99Ms);
