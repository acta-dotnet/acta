namespace Acta;

/// <summary>
/// The attempt a handler-originated write belongs to: its job, its execution number, and the worker that claimed it.
/// Every such write locks the job's <c>jobs_state</c> row and lands only while that row is Executing under this
/// execution and worker, in the transaction that writes (docs/sql-internals.md, "Execution fence").
/// </summary>
internal readonly record struct ExecutionFence(long JobId, int ExecutionNumber, int WorkerId);
