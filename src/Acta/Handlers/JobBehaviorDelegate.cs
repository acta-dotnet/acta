namespace Acta;

/// <summary>
/// Continuation in a job pipeline-behavior chain. Invoking it runs the next behavior, or the generated
/// handler invoker at the innermost link. It captures the per-attempt request, context, and
/// cancellation token, so a behavior continues the pipeline with no arguments.
/// </summary>
public delegate ValueTask<JobHandlerOutput> JobBehaviorDelegate();
