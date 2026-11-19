namespace Acta;

/// <summary>
/// What one handler invocation produced. <see cref="HasResult"/> comes from the descriptor; when it is set, a
/// <c>null</c> <see cref="Result"/> stores no result, as a handler that declares none does.
/// </summary>
public readonly record struct JobHandlerOutput(bool HasResult, object? Result);
