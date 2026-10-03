namespace Acta.Runtime;

/// <summary>
/// Composed key plus the per-hold CAS token minted at acquire. A token, not a counter:
/// delete-and-reacquire can never re-mint it, so a stale holder cannot free or extend a
/// successor's lock.
/// </summary>
internal readonly record struct LockToken(string Key, Guid HoldToken);
