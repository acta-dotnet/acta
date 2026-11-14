namespace Acta;

/// <summary>
/// The base of every exception Acta defines, so one catch takes everything Acta throws of its own. Its one sub-base
/// that is not an error is <see cref="JobControlException"/>: a wait, sleep, reschedule or ctx control call ends the
/// attempt by throwing one, and the worker turns it into the job's next state. Argument checks, misuse, parse formats
/// and cancellation throw .NET's own exceptions.
/// </summary>
public abstract class ActaException : Exception
{
    protected ActaException(string? message)
        : base(message) { }

    protected ActaException(string? message, Exception? innerException)
        : base(message, innerException) { }
}
