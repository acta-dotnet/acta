namespace Acta.Modules.Execution;

/// <summary>
/// Builds the per-attempt logging-scope state. <see cref="JobExecutor"/> opens it once around an
/// attempt so every framework and handler log line emitted while the job runs carries the job
/// identity. Because loggers from one factory share an external scope provider, opening the scope on
/// the runtime logger also stamps the handler's own <c>ILogger&lt;T&gt;</c> output.
/// </summary>
/// <remarks>
/// The key strings are the operator-facing log property names, the same contract a structured-log
/// query filters on, so they are pinned by <c>JobLogScopeTests</c>.
/// </remarks>
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
            new("JobNamespace", jobNamespace),
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
