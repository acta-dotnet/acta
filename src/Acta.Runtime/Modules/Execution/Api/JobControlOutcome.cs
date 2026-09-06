namespace Acta.Runtime.Modules.Execution.Api;

/// <summary>
/// The actor, reason, and optional expected version an operator stamps on a control transition. The job
/// control verbs share this input; the public <see cref="IJobs"/> surface builds it itself so a caller
/// cannot forge the actor. A null <c>ExpectedVersion</c> writes unconditionally.
/// </summary>
internal sealed record JobControlInput(
    JobControlActor Actor,
    JobEventReasonCode ReasonCode,
    string? ReasonMessage,
    int? ExpectedVersion = null
);

/// <summary>
/// Result of a control transition: the action plus the job's status and row version after the attempt.
/// Shared by the job control verbs, whose routines all return one (action, status_code, version) row.
/// </summary>
internal sealed record JobControlOutcome(JobControlActionInternal Action, JobStatusCode? Status, int? Version);

/// <summary>
/// Flat row of the control routines that carry no expected version (purge, input amend, signal raise);
/// their result sets are (action, status_code) and the outcome's version is null.
/// </summary>
internal sealed record JobControlActionRow(JobControlActionInternal Action, JobStatusCode? Status)
{
    public JobControlOutcome ToOutcome() => new(Action, Status, null);
}

/// <summary>
/// Internal mirror of <see cref="ControlAction"/>; the facade maps it to the public enum.
/// </summary>
internal enum JobControlActionInternal : byte
{
    /// <summary>The transition was applied.</summary>
    Applied = 1,

    /// <summary>No job matched the id.</summary>
    NotFound = 2,

    /// <summary>The current status did not permit the transition.</summary>
    Rejected = 3,

    /// <summary>A non-null expected version did not match the row's version.</summary>
    VersionConflict = 5,
}
