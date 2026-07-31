namespace Acta.Modules.Alerting;

/// <summary>
/// The alert projector's poison classification: raised only for the two proven malformed-ledger
/// shapes it deliberately skips past (the subject job no longer exists, or a stored field fails
/// canonicalization). Any other exception fails the <c>sys.alerts</c> pass, so the shared event
/// cursor never advances over an event that hit an unexpected error.
/// </summary>
internal sealed class AlertProjectionDataException(string reason, string message, Exception inner) : Exception(message, inner)
{
    /// <summary>Skip classification recorded on the durable outcome: "unknown-job" or "invalid-event".</summary>
    public string Reason { get; } = reason;
}
