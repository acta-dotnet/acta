namespace Acta.Features.Execution.ChildLatches;

/// <summary>
/// One stale child-done latch: a parent's Pending <c>sys.child.{id}</c> slot whose child is already
/// terminal (its raise was lost to a crash, or the slot was re-armed after a state reset) or whose
/// child row no longer exists. <see cref="ChildStatus"/> is null when the row is gone.
/// </summary>
internal sealed record StaleChildLatch(long ParentJobId, long ChildJobId, JobStatusCode? ChildStatus);
