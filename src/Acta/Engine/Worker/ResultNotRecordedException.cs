namespace Acta;

/// <summary>
/// A handler returned a result that cannot be recorded: null, or one whose serialization threw. The job fails for
/// good on that attempt, because a retry would run the finished handler again. No handler code runs after its own
/// return, so nothing needs to catch this; a step in the same position throws the public <see cref="StepExhaustedException"/>.
/// </summary>
internal sealed class ResultNotRecordedException(string message, Exception? innerException = null) : Exception(message, innerException);
