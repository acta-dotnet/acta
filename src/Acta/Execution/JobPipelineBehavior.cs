namespace Acta;

/// <summary>
/// Continuation in a job pipeline-behavior chain. Invoking it runs the next behavior, or the generated
/// handler invoker at the innermost link. It captures the per-attempt request, context, and
/// cancellation token, so a behavior continues the pipeline with no arguments.
/// </summary>
public delegate ValueTask<JobHandlerInvocationResult> JobBehaviorDelegate();

/// <summary>
/// A behavior wrapping the handler invocation for one attempt. Behaviors run inside the attempt, after
/// the execution starts and before it completes, and do not own durable state: claim, start, retry,
/// lease, and completion stay framework-owned.
/// </summary>
/// <remarks>
/// Behaviors are registered in deterministic order via <c>jobs.AddPipelineBehavior&lt;T&gt;()</c>; the
/// first registered is the outermost link, the last is closest to the handler. Each is resolved from
/// the per-attempt dependency-injection scope, so a behavior may take constructor-injected
/// dependencies, including the scoped <see cref="JobContext"/>. A behavior runs once per attempt, so it
/// runs again on every retry; keep behaviors safe to re-run.
/// </remarks>
public interface IJobPipelineBehavior
{
    /// <summary>
    /// Wraps the next link. Call <paramref name="next"/> once to continue the chain and the eventual
    /// handler.
    /// </summary>
    /// <remarks>
    /// A behavior may skip <paramref name="next"/> to short-circuit, but then it owns producing a valid
    /// <see cref="JobHandlerInvocationResult"/>. Calling <paramref name="next"/> more than once throws,
    /// because a second call would re-run the handler within the same attempt. Exceptions (including
    /// <c>JobControlException</c> subclasses) must propagate so the runtime outcome ladder classifies
    /// them; do not swallow them into a fake success.
    /// </remarks>
    ValueTask<JobHandlerInvocationResult> InvokeAsync(object request, JobContext context, JobBehaviorDelegate next, CancellationToken ct);
}
