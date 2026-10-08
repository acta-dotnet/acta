namespace Acta;

/// <summary>One lock to take for a job, held for the time to live unless extended.</summary>
internal sealed record AcquireLockCommand(string Key, TimeSpan Ttl, long OwnerJobId);
