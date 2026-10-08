namespace Acta.Runtime;

/// <summary>One (ordinal, id, ref, action) row of the <c>enqueue_jobs</c> routine, one per input ordinal.</summary>
[DbProjection]
internal readonly record struct EnqueueJobsRow(int Ordinal, long JobId, Guid JobRef, EnqueueJobAction Action);
