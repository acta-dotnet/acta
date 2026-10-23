namespace Acta;

/// <summary>
/// Thrown by <see cref="IJobs.PurgeAsync"/> when the job still has a failure, orphan, terminal failure, or recovery the
/// <c>sys.alerts</c> projector has not processed, or a skipped alert event awaiting replay: the projector reads the job
/// row to raise or resolve its alert, so the purge waits for it and writes nothing. Retry once the namespace's
/// <c>sys.alerts</c> pass has run. The provider exception is preserved as the inner exception.
/// </summary>
/// <remarks>Build the refusal with the provider exception as inner.</remarks>
public sealed class JobPurgeBlockedException(string message, Exception? innerException = null) : Exception(message, innerException);
