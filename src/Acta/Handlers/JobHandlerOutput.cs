namespace Acta;

/// <summary>
/// What one handler invocation produced. <see cref="HasResult"/> comes from the descriptor; when it is
/// set, a <c>null</c> <see cref="Result"/> fails the attempt. Acta results are never null - use
/// <c>Task</c> (no result) for a no-result handler, or wrap optional data in a non-null object.
/// </summary>
public readonly record struct JobHandlerOutput(bool HasResult, object? Result);
