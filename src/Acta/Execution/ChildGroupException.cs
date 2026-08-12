namespace Acta;

/// <summary>
/// Thrown by the ThrowIfAnyFailed escalation on a Join, Parallel, or Map outcome when at least one
/// child job did not land Succeeded. Carries the non-succeeded child outcomes (failed or cancelled); the
/// group wait itself never throws.
/// </summary>
/// <remarks>
/// Builds the exception from the non-succeeded child outcomes that triggered the escalation.
/// </remarks>
public sealed class ChildGroupException(IReadOnlyList<ChildJobOutcome> failed) : Exception(BuildMessage(failed))
{
    /// <summary>The non-succeeded child outcomes (failed or cancelled).</summary>
    public IReadOnlyList<ChildJobOutcome> Failed { get; } = failed;

    private static string BuildMessage(IReadOnlyList<ChildJobOutcome> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);
        var ids = string.Join(", ", failed.Select(o => $"{o.ChildJobId}:{o.Status}"));
        return $"{failed.Count} child job(s) did not succeed: {ids}.";
    }
}
