namespace Acta;

/// <summary>
/// A name a handler records (a step, variable, signal, timer, or child) that breaks the name rule. Non-retryable: the
/// same name fails every attempt, so the job fails at once instead of spending its retry budget.
/// </summary>
internal sealed class InvalidDurableNameException(string message, string paramName) : ActaException($"{message} (Parameter '{paramName}')");
