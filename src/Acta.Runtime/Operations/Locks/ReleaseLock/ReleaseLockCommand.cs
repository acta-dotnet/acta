namespace Acta.Runtime;

/// <summary>One held lock to give back.</summary>
internal sealed record ReleaseLockCommand(LockToken Token);
