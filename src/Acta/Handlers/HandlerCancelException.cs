namespace Acta;

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Cancelled</c> (a non-failure stop). Thrown by
/// <c>ctx.CancelAsync</c>; the attempt is not retried and the failure budget is untouched.
/// </summary>
public sealed class HandlerCancelException : HandlerControlException
{
    internal HandlerCancelException(string? reasonMessage)
        : base(reasonMessage) { }
}
