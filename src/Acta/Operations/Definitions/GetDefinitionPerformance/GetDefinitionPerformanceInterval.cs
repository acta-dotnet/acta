namespace Acta;

/// <summary>One interval of the range, with the executions that finished in it.</summary>
/// <param name="StartAtUtc">The interval's start, inclusive; it ends where the next one starts, or at the range's toUtc.</param>
/// <param name="Succeeded">The succeeded executions that finished in the interval.</param>
/// <param name="Failed">The failed executions that finished in the interval.</param>
public sealed record GetDefinitionPerformanceInterval(
    DateTime StartAtUtc,
    GetDefinitionPerformanceExecutions Succeeded,
    GetDefinitionPerformanceExecutions Failed
);
