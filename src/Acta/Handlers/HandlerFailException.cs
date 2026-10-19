namespace Acta;

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Failed</c>. Thrown by <c>ctx.FailAsync</c>; the
/// attempt is not retried and the failure budget is untouched. Distinct from a thrown exception, which
/// is recorded as an unhandled failure.
/// </summary>
/// <remarks>Initializes the fail control with an optional <paramref name="reasonMessage"/>.</remarks>
public sealed class HandlerFailException(string? reasonMessage = null) : HandlerControlException(reasonMessage) { }
