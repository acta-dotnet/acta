namespace Acta;

/// <summary>
/// Thrown by <see cref="JobContext"/> <c>RunWithLockAsync</c> when the lock could not be acquired
/// within the caller's timeout budget. The guarded action never ran.
/// </summary>
/// <remarks>
/// Creates the exception for the user-supplied <paramref name="key"/> and the elapsed
/// <paramref name="timeout"/> budget.
/// </remarks>
public sealed class LockAcquisitionTimeoutException(string key, TimeSpan timeout)
    : Exception($"Could not acquire lock '{key}' within {timeout}.")
{
    /// <summary>The user-supplied lock key that could not be acquired (not the internal composed key).</summary>
    public string Key { get; } = key;

    /// <summary>The timeout budget that elapsed before acquisition gave up.</summary>
    public TimeSpan Timeout { get; } = timeout;
}
