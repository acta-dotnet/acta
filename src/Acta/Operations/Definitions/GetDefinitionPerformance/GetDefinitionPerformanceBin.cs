namespace Acta;

/// <summary>
/// One bin of a duration histogram. Bins are one millisecond wide below 10 ms and ten to a decade above it, from 10, 13,
/// 16, 20, 25, 32, 40, 50, 63 and 80 ms.
/// </summary>
/// <param name="FromMs">The shortest duration the bin holds, in milliseconds.</param>
/// <param name="ToMs">The duration the bin ends before, in milliseconds, which is the next bin's fromMs.</param>
/// <param name="ExecutionCount">How many executions fell in the bin.</param>
public sealed record GetDefinitionPerformanceBin(long FromMs, long ToMs, long ExecutionCount);
