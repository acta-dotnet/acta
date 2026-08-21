namespace Acta;

/// <summary>
/// Thrown by the ThrowIfAnyFailed escalation on a Join, Parallel, Map, or bounded-group outcome when at
/// least one child job did not land Succeeded. Carries the non-succeeded child outcomes (failed,
/// cancelled, or timed out); the group wait itself never throws.
/// </summary>
/// <remarks>
/// Builds the exception from the non-succeeded child outcomes that triggered the escalation.
/// </remarks>
public sealed class ChildGroupException(IReadOnlyList<ChildJobOutcome> failed) : Exception(BuildMessage(failed))
{
    /// <summary>The non-succeeded child outcomes (failed, cancelled, or timed out).</summary>
    public IReadOnlyList<ChildJobOutcome> Failed { get; } = failed;

    // A timed-out child is labelled by the wait that gave up on it, not by the Cancelled status Acta
    // then drove it to: an operator reading this message needs to know the group ran out of time, and
    // "Cancelled" alone reads as somebody's deliberate stop.
    private static string BuildMessage(IReadOnlyList<ChildJobOutcome> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var ids = string.Join(", ", failed.Select(o => $"{o.ChildJobId}:{(o.TimedOut ? "TimedOut" : o.Status.ToString())}"));
        return $"{failed.Count} child job(s) did not succeed: {ids}.";
    }
}
