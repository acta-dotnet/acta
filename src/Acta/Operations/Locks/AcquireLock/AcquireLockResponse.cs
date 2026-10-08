namespace Acta;

/// <summary>The hold taken, carried to extend and release; null when the lock is held.</summary>
internal readonly record struct AcquireLockResponse(LockToken? Token);
