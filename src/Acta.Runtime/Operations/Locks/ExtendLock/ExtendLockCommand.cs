namespace Acta.Runtime;

/// <summary>One held lock to keep for another time to live.</summary>
internal sealed record ExtendLockCommand(LockToken Token, TimeSpan Ttl);
