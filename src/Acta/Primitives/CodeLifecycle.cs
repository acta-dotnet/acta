namespace Acta;

/// <summary>
/// Per-code lifecycle flag. Active and Deprecated are writable; Retired and Fallback are read-only.
/// All four appear in <c>FromId</c>, <c>IsKnownId</c>, <c>Manifest</c>, and <c>code-family docs</c>;
/// <c>IsWritableId</c> excludes Retired and Fallback, and a column's IN-list CHECK excludes Fallback.
/// </summary>
public enum CodeLifecycle : byte
{
    /// <summary>Current. Writable; recommended.</summary>
    Active = 1,

    /// <summary>Writable but flagged for migration; operators should plan replacement.</summary>
    Deprecated = 2,

    /// <summary>Read-only. Retained so historical rows remain interpretable; new writes rejected.</summary>
    Retired = 3,

    /// <summary>Never written: the id-0 member an id this build does not know reads back as, so a row a newer Acta wrote decodes during a rolling deploy.</summary>
    Fallback = 4,
}
