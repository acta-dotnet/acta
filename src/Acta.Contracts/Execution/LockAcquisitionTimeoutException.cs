namespace Acta;

/// <summary>
/// Thrown by <see cref="JobContext"/> <c>RunWithLockAsync</c> when the lock could not be acquired
/// within the caller's timeout budget. The guarded action never ran.
/// </summary>
public sealed class LockAcquisitionTimeoutException : Exception
{
    /// <summary>
    /// Creates the exception for the user-supplied <paramref name="key"/> and the elapsed
    /// <paramref name="timeout"/> budget.
    /// </summary>
    public LockAcquisitionTimeoutException(string key, TimeSpan timeout)
        : base($"Could not acquire lock '{key}' within {timeout}.")
    {
        Key = key;
        Timeout = timeout;
    }

    /// <summary>The user-supplied lock key that could not be acquired (not the internal composed key).</summary>
    public string Key { get; }

    /// <summary>The timeout budget that elapsed before acquisition gave up.</summary>
    public TimeSpan Timeout { get; }
}
