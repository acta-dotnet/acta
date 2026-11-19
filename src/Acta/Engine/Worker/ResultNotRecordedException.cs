namespace Acta;

/// <summary>
/// A handler returned a result that cannot be recorded: one whose serialization threw, or one past the result ceiling. The
/// job fails for good on that attempt, because a retry would run the finished handler again. No handler code runs after
/// its own return, so nothing needs to catch this; a step in the same position throws the public
/// <see cref="StepFailedException"/>.
/// </summary>
internal sealed class ResultNotRecordedException(string message, Exception? innerException = null) : ActaException(message, innerException);
