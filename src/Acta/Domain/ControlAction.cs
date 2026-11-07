namespace Acta;

/// <summary>
/// What a control did: applied; already-in-state, when the target was already as asked; rejected, when the target's state refuses the
/// call; version-conflict, when the expected version is stale; not-found; or accepted, when the work is stored for the next pass to apply.
/// </summary>
public enum ControlAction : byte
{
    /// <summary>The transition was applied.</summary>
    Applied = 1,

    /// <summary>No target matched the lookup.</summary>
    NotFound = 2,

    /// <summary>The target's current state did not permit the transition, so nothing was written.</summary>
    Rejected = 3,

    /// <summary>The command was accepted and durably parked; the applying pass performs the transition on its next run. Used by verbs that stage work instead of transitioning state directly (the <see cref="IOutbox"/> controls).</summary>
    Accepted = 4,

    /// <summary>A non-null expectedVersion did not match the target, so nothing was written; <c>Version</c> is the current version.</summary>
    VersionConflict = 5,

    /// <summary>The target was already in the requested state, so nothing was written; <c>Version</c> is the current version. Answered by the tenant and namespace suspend and resume verbs.</summary>
    AlreadyInState = 6,
}
