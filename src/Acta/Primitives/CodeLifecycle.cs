namespace Acta;

/// <summary>
/// Per-code lifecycle flag. Active and Deprecated are writable; Retired is read-only.
/// All three appear in <c>FromId</c>, <c>IsKnownId</c>, <c>Manifest</c>, and <c>code-family docs</c>;
/// <c>IsWritableId</c> excludes Retired only.
/// </summary>
public enum CodeLifecycle : byte
{
    /// <summary>Current. Writable; recommended.</summary>
    Active = 1,

    /// <summary>Writable but flagged for migration; operators should plan replacement.</summary>
    Deprecated = 2,

    /// <summary>Read-only. Retained so historical rows remain interpretable; new writes rejected.</summary>
    Retired = 3,
}
