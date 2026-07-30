using Acta.Modules.Execution.Api;

namespace Acta.Modules.Execution.Api;

/// <summary>
/// The actor and reason an operator stamps on a control transition. The job control verbs share this
/// input; the public <see cref="IJobs"/> surface builds it itself so a caller cannot forge the actor.
/// </summary>
internal sealed record JobControlInput(JobControlActor Actor, JobEventReasonCode ReasonCode, string? ReasonMessage);

/// <summary>
/// Result of a control transition: the action and the job's status after the attempt. Shared by the job
/// control verbs, whose routines all return one (action, status_code) row.
/// </summary>
internal sealed record JobControlOutcome(JobControlActionInternal Action, JobStatusCode? Status);

/// <summary>
/// Internal mirror of <see cref="JobControlAction"/>; the facade maps it to the public enum.
/// </summary>
internal enum JobControlActionInternal : byte
{
    /// <summary>The transition was applied.</summary>
    Applied = 1,

    /// <summary>No job matched the id.</summary>
    NotFound = 2,

    /// <summary>The current status did not permit the transition.</summary>
    Rejected = 3,
}
