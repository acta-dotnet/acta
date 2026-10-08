namespace Acta;

/// <summary>
/// Continuation in a job pipeline-behavior chain. Invoking it runs the next behavior, or the generated
/// handler invoker at the innermost link. It captures the per-attempt request, context, and
/// cancellation token, so a behavior continues the pipeline with no arguments.
/// </summary>
public delegate ValueTask<JobHandlerOutput> JobBehaviorDelegate();

/// <summary>
/// A behavior wrapping the handler invocation for one attempt. Behaviors run inside the attempt, after the execution
/// starts and before it completes, and own no durable state: claim, start, retry, lease, and completion stay
/// framework-owned. They are registered in order via <c>jobs.AddPipelineBehavior&lt;T&gt;()</c>, the first outermost
/// and the last closest to the handler, and each resolves from the per-attempt DI scope, so it may take
/// constructor-injected dependencies including the scoped <see cref="JobContext"/>. A behavior runs once per attempt,
/// so it runs again on every retry; keep it safe to re-run.
/// </summary>
public interface IJobPipelineBehavior
{
    /// <summary>
    /// Wraps the next link; call <paramref name="next"/> once to continue the chain and reach the handler. A behavior
    /// may skip it to short-circuit, but then owns producing a valid <see cref="JobHandlerOutput"/>; calling it more
    /// than once throws, since a second call would re-run the handler within the attempt. Exceptions, including
    /// <c>JobControlException</c> subclasses, must propagate so the runtime classifies them; never swallow one into a
    /// fake success.
    /// </summary>
    ValueTask<JobHandlerOutput> InvokeAsync(object request, JobContext context, JobBehaviorDelegate next, CancellationToken ct);
}
