namespace Acta;

/// <summary>
/// Outcome of an <see cref="IJobs"/> control verb (Cancel / Pause / Resume / Restart). Carries the
/// targeted <see cref="JobId"/>, the coarse <see cref="ControlAction"/>, and the job's
/// <see cref="Status"/> after the attempt.
/// </summary>
/// <remarks>
/// <see cref="Status"/> carries the new target status on <see cref="ControlAction.Applied"/>, the
/// current status that blocked the transition on <see cref="ControlAction.Rejected"/>, and
/// <c>null</c> on <see cref="ControlAction.NotFound"/>.
/// </remarks>
/// <param name="JobId">The targeted job's id; <c>0</c> when the lookup matched no row.</param>
/// <param name="Action">Whether the control transition was applied, rejected, or the job was absent.</param>
/// <param name="Status">The job's status after the attempt; see remarks.</param>
public sealed record JobControlResult(long JobId, ControlAction Action, JobStatusCode? Status);

/// <summary>
/// Coarse outcome of an <see cref="IJobs"/> control verb.
/// </summary>
public enum ControlAction : byte
{
    /// <summary>The transition was applied; <c>Status</c> is the new target status.</summary>
    Applied = 1,

    /// <summary>No job matched the lookup; <c>Status</c> is null.</summary>
    NotFound = 2,

    /// <summary>The job's current status did not permit the transition; <c>Status</c> is that blocking status.</summary>
    Rejected = 3,

    /// <summary>The command was accepted and durably parked; the applying pass performs the transition on its next run. Used by verbs that stage work instead of transitioning state directly (the <see cref="IOutbox"/> controls).</summary>
    Accepted = 4,
}
