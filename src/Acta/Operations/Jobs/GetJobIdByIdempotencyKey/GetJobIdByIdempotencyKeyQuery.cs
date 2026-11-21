namespace Acta;

/// <summary>The idempotency key to resolve within a namespace, both already canonical.</summary>
internal sealed record GetJobIdByIdempotencyKeyQuery(string JobNamespace, string IdempotencyKey);
