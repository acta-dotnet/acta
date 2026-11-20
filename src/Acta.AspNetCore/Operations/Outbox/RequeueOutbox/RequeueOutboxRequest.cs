namespace Acta.AspNetCore;

/// <summary>
/// Optional body of the outbox requeue: the quarantined rows to make deliverable again, and a reason. Without jobRefs it acts on every
/// quarantined row of the namespace.
/// </summary>
/// <param name="JobRefs">The job refs of the quarantined rows to act on, at least one; omitted or null acts on every quarantined row of the
/// namespace.</param>
/// <param name="ReasonMessage">Why the operator made this call, recorded on the event the call writes.</param>
internal sealed record RequeueOutboxRequest(IReadOnlyList<JobRef>? JobRefs = null, string? ReasonMessage = null) : IControlRequest;
