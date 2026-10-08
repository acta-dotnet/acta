namespace Acta;

/// <summary>
/// Builds the per-attempt logging-scope state. <see cref="JobExecutor"/> opens it once around an attempt so every
/// framework and handler log line emitted while the job runs carries the job identity; loggers from one factory share
/// an external scope provider, so opening the scope on the runtime logger also stamps the handler's own
/// <c>ILogger&lt;T&gt;</c> output. The key strings are the operator-facing log property names a structured-log query
/// filters on, pinned by <c>JobLogScopeTests</c>.
/// </summary>
internal static class JobLogScope
{
    public static IReadOnlyList<KeyValuePair<string, object>> For(
        long jobId,
        string jobName,
        string jobNamespace,
        int executionNumber,
        int workerId,
        string? correlationKey = null
    )
    {
        var state = new List<KeyValuePair<string, object>>(6)
        {
            new("JobId", jobId),
            new("JobName", jobName),
            new("Namespace", jobNamespace),
            new("ExecutionNumber", executionNumber),
            new("WorkerId", workerId),
        };

        if (correlationKey is not null)
        {
            state.Add(new("CorrelationKey", correlationKey));
        }

        return state;
    }
}
