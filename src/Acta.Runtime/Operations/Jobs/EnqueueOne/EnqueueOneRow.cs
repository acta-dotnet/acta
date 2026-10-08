namespace Acta.Runtime;

/// <summary>The (ordinal, id, ref, action) row the <c>enqueue_one</c> routine returns; its ordinal is always 0.</summary>
[DbProjection]
internal readonly record struct EnqueueOneRow(int Ordinal, long JobId, Guid JobRef, JobEnqueueAction Action);
