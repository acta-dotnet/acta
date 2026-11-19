namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync&lt;TResult&gt;</c> on replay when a previously succeeded step's recorded result cannot
/// be read as the current <c>TResult</c>: stored bytes that cannot be deserialized into it, or no result, which a body
/// that returned null records, read as a non-nullable value type. Changing a step's result type while jobs are in flight
/// is a breaking change, surfaced here instead of as a silently wrong value. Names the step, the
/// <paramref name="expectedType"/>, and wraps the cause in <paramref name="innerException"/>.
/// </summary>
public sealed class StepResultContractMismatchException(string stepName, Type expectedType, Exception innerException)
    : ActaException(
        $"Step '{stepName}' recorded a result that cannot be read as '{expectedType}'. "
            + "The step's result contract changed while the job was in flight (a breaking change).",
        innerException
    )
{
    /// <summary>The name of the step whose recorded result could not be read.</summary>
    public string StepName { get; } = stepName;

    /// <summary>The current result type the recorded result could not be read as.</summary>
    public Type ExpectedType { get; } = expectedType;
}
