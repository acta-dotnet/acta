namespace Acta;

/// <summary>
/// A schedule's source could not be evaluated: its expression, its zone, or a calendar it names gave no instant or
/// window that can be used. The admitter parks such a schedule as an evaluation error; a preview throws this, which the
/// HTTP layer answers as a 422 problem carrying the message.
/// </summary>
public sealed class ScheduleEvaluationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
