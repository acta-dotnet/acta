namespace Acta;

/// <summary>
/// Thrown from <c>ctx.RunStepAsync&lt;TResult&gt;</c> on replay when a previously-<c>Succeeded</c>
/// step's stored result bytes cannot be deserialized into the current <c>TResult</c>.
/// Renaming a step's result type while jobs are in flight is a breaking change; this surfaces it
/// clearly instead of returning a silently-wrong value.
/// </summary>
/// <remarks>
/// Creates the exception for step <paramref name="stepName"/> whose stored result could not
/// be read as <paramref name="expectedType"/>, wrapping the serializer's <paramref name="innerException"/>.
/// </remarks>
public sealed class StepResultContractMismatchException(string stepName, Type expectedType, Exception innerException)
    : Exception(
        $"Step '{stepName}' has a stored result that cannot be deserialized into '{expectedType}'. "
            + "The step's result contract changed while the job was in flight (a breaking change).",
        innerException
    )
{
    /// <summary>The step slot name whose stored result failed to deserialize.</summary>
    public string StepName { get; } = stepName;

    /// <summary>The current result type the stored bytes could not be read as.</summary>
    public Type ExpectedType { get; } = expectedType;
}
