namespace Acta;

/// <summary>
/// How long a definition's executions took over a range and how many failed, from the events the ledger still holds. It
/// counts an execution that ended in success or failure and was recorded; a yielded or orphaned one is not counted. A
/// duration is one handler invocation as its worker timed it, truncated to two significant figures.
/// </summary>
/// <param name="FromUtc">The range's start, inclusive, truncated to the millisecond.</param>
/// <param name="ToUtc">The range's end, exclusive, truncated to the millisecond.</param>
/// <param name="IntervalSeconds">The length of one interval in seconds.</param>
/// <param name="ExecutionsHasMore">Whether the read reached its limit of 500,000 executions. It then counted only the newest ones, back to oldestAtUtc, and an interval that starts at or before oldestAtUtc is cut short or was not read, which its counts do not say. A range that holds exactly the limit sets it too.</param>
/// <param name="OldestAtUtc">When the oldest execution counted finished; null when none was counted.</param>
/// <param name="Succeeded">The succeeded executions of the whole range.</param>
/// <param name="Failed">The failed executions of the whole range, whether or not the job ran again.</param>
/// <param name="SucceededBins">The succeeded executions by duration, from the first bin that holds one to the last, empty bins between them included; at most 94.</param>
/// <param name="FailedBins">The failed executions by duration, from the first bin that holds one to the last, empty bins between them included; at most 94.</param>
/// <param name="Intervals">Every interval of the range, oldest first, at most 60.</param>
public sealed record GetDefinitionPerformanceResponse(
    DateTime FromUtc,
    DateTime ToUtc,
    int IntervalSeconds,
    bool ExecutionsHasMore,
    DateTime? OldestAtUtc,
    GetDefinitionPerformanceExecutions Succeeded,
    GetDefinitionPerformanceExecutions Failed,
    IReadOnlyList<GetDefinitionPerformanceBin> SucceededBins,
    IReadOnlyList<GetDefinitionPerformanceBin> FailedBins,
    IReadOnlyList<GetDefinitionPerformanceInterval> Intervals
);
