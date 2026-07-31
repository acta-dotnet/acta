namespace Acta.Runtime.Modules.Execution.Api;

/// <summary>
/// Execution's declared alerting seam: <c>JobContext.AlertAsync</c> raises manual alert intents
/// through this and knows nothing about alert policy or persistence. Alerting implements it
/// (<c>AlertStoreSink</c>), owning the origin/kind codes, the default channel, and the
/// dedupe-window bucketing.
/// </summary>
internal interface IAlertSink
{
    Task RaiseManualAsync(
        string jobNamespace,
        long jobId,
        AlertSeverityCode severityCode,
        string title,
        string message,
        string? channelName,
        string? deduplicationKey,
        CancellationToken ct
    );
}
