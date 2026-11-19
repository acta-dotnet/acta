namespace Acta;

/// <summary>
/// Thrown by <c>JobOutcome&lt;TResult&gt;.ValueOrThrow()</c> when the job succeeded and stored no result: its handler,
/// declared with an optional result such as <c>Task&lt;Customer?&gt;</c>, returned null, which stores nothing, or a typed
/// wait read a job whose definition declares no result. Read an optional result with <c>TryGetValue</c>. Left uncaught in
/// a handler it fails the job at once, since a replay reads the same outcome. Carries the <see cref="JobId"/> and the
/// public <see cref="JobRef"/>.
/// </summary>
public sealed class JobResultMissingException : ActaException
{
    /// <summary>A job that succeeded and stored no result.</summary>
    public JobResultMissingException(long jobId, JobRef jobRef)
        : this(jobId, jobRef, jobName: null) { }

    /// <summary>A job that succeeded and stored no result, named by its job name when known.</summary>
    internal JobResultMissingException(long jobId, JobRef jobRef, string? jobName)
        : base(
            jobName is null
                ? $"Job {jobRef} succeeded and stored no result. Read an optional result with TryGetValue."
                : $"Job {jobRef} ('{jobName}') succeeded and stored no result. Read an optional result with TryGetValue."
        )
    {
        JobId = jobId;
        JobRef = jobRef;
    }

    public long JobId { get; }
    public JobRef JobRef { get; }
}
