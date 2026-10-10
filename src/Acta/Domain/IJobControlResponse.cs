namespace Acta;

/// <summary>
/// What a job control verb did. <see cref="Status"/> is the new status on <see cref="ControlAction.Applied"/> and the
/// blocking one on <see cref="ControlAction.Rejected"/> and <see cref="ControlAction.VersionConflict"/>; the version
/// follows the same rule on the verbs that carry one. Both are null wherever the verb settled without reading the row,
/// <c>NotFound</c> included.
/// </summary>
public interface IJobControlResponse : IControlResponse
{
    /// <summary>The targeted job's id, never serialized; <c>0</c> when the lookup matched no row.</summary>
    long JobId { get; }

    /// <summary>The job's status after the attempt.</summary>
    JobStatusCode? Status { get; }
}
