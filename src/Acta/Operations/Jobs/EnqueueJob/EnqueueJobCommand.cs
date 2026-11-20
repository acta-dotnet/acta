using System.Data.Common;

namespace Acta;

/// <summary>
/// One job to enqueue, through the caller's <c>Transaction</c> when it is set, and only while <c>ParentFence</c> holds
/// the parent when a running handler starts it as a child. <c>JobRef</c> is the ref to give the job; null mints one.
/// </summary>
internal sealed record EnqueueJobCommand(
    EnqueueJobRequest Request,
    DbTransaction? Transaction = null,
    ExecutionFence? ParentFence = null,
    Guid? JobRef = null
);
