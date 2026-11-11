namespace Acta;

/// <summary>
/// A handler or step body ran to its end and its result could not be recorded: it was null, could not be serialized,
/// or, for a step, was over the inline cap. The attempt fails for good, because a retry would run the finished body
/// again. An at-most-once step is left started, not failed, so a restart of the job finds it interrupted and does not
/// run it either.
/// </summary>
internal sealed class ResultNotRecordedException(string message, Exception? innerException = null) : Exception(message, innerException);
