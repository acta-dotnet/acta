namespace Acta.Runtime;

/// <summary>The (ordinal, id, ref, action) row the <c>enqueue_job</c> routine returns; its ordinal is always 0.</summary>
[DbProjection]
internal readonly record struct EnqueueJobRow(int Ordinal, long JobId, Guid JobRef, EnqueueJobAction Action);
