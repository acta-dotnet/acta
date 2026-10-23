using System.Data.Common;

namespace Acta;

/// <summary>
/// One job to enqueue, through the caller's <c>Transaction</c> when it is set, and only while <c>ParentFence</c> holds
/// the parent when a running handler starts it as a child.
/// </summary>
internal sealed record EnqueueJobCommand(EnqueueJobRequest Request, DbTransaction? Transaction = null, ExecutionFence? ParentFence = null);
