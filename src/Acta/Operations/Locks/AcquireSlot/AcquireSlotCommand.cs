namespace Acta;

/// <summary>One slot to take for a job among the first <c>Limit</c> slots of a counted key.</summary>
internal sealed record AcquireSlotCommand(string KeyPrefix, int Limit, TimeSpan Ttl, long OwnerJobId);
