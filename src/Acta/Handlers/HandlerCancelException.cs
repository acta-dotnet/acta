namespace Acta;

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Cancelled</c> (a non-failure stop). Thrown by
/// <c>ctx.CancelAsync</c>; the attempt is not retried and the failure budget is untouched.
/// </summary>
/// <remarks>Initializes the cancel control with an optional <paramref name="reasonMessage"/>.</remarks>
public sealed class HandlerCancelException(string? reasonMessage = null) : HandlerControlException(reasonMessage) { }
