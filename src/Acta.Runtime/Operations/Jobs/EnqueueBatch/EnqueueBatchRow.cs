namespace Acta.Runtime;

/// <summary>One (ordinal, id, ref, action) row of the <c>enqueue_batch</c> routine, one per input ordinal.</summary>
[DbProjection]
internal readonly record struct EnqueueBatchRow(int Ordinal, long JobId, Guid JobRef, JobEnqueueAction Action);
