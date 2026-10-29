using System.Text.Json.Serialization;

namespace Acta;

/// <summary>
/// What happens to a scheduled definition's schedules when a firing fails for good: its attempts are
/// spent, or an exception is not retryable.
/// </summary>
[JsonConverter(typeof(FiringFailurePolicyCodeJsonConverter))]
[CodeKind("firing-failure-policy")]
public enum FiringFailurePolicyCode : byte
{
    [Code("unspecified", "Firing failure policy not recognized by this build; the row was written by a newer Acta.")]
    Unspecified = 0,

    /// <summary>The schedules stay scheduled, so the next occurrence fires.</summary>
    [Code("continue", "Default. A firing that fails for good leaves the definition's schedules scheduled; the next occurrence fires.")]
    Continue = 10,

    /// <summary>
    /// Every schedule of the definition pauses with a pause message, and stays paused until an operator
    /// resumes it.
    /// </summary>
    [Code(
        "pause-scheduling",
        "A firing that fails for good pauses every schedule of the definition with a pause message until an operator resumes them."
    )]
    PauseScheduling = 20,
}
