namespace Acta;

/// <summary>Whether the hold was kept; false when its token no longer matches.</summary>
internal readonly record struct ExtendLockResponse(bool Extended);
