namespace Acta;

/// <summary>
/// Ends the current Job as a deliberate terminal <c>Failed</c>. Thrown by <c>ctx.FailAsync</c>; the
/// attempt is not retried and the failure budget is untouched. Distinct from a thrown exception, which
/// is recorded as an unhandled failure.
/// </summary>
public sealed class HandlerFailException : HandlerControlException
{
    internal HandlerFailException(string? reasonMessage)
        : base(reasonMessage) { }
}
