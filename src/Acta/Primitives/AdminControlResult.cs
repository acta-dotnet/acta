namespace Acta;

/// <summary>Coarse outcome of an admin control verb (tenant/namespace suspend/resume/update).</summary>
public enum AdminControlAction : byte
{
    /// <summary>The transition was applied; Version is the new row version.</summary>
    Applied = 1,

    /// <summary>No row matched the key/name; Version is null.</summary>
    NotFound = 2,

    /// <summary>The row was already in the requested state; idempotent no-op, Version is the current version.</summary>
    AlreadyInState = 3,

    /// <summary>An update CAS saw a stale expected version; Version is the row's current version.</summary>
    VersionConflict = 4,
}

/// <summary>Result of an admin control transition: the action plus the row version after the attempt.</summary>
public sealed record AdminControlResult(AdminControlAction Action, int? Version);
