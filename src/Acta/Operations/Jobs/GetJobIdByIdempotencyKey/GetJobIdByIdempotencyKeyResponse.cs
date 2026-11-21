namespace Acta;

/// <summary>The id of the root job holding the idempotency key.</summary>
internal sealed record GetJobIdByIdempotencyKeyResponse(long JobId);
