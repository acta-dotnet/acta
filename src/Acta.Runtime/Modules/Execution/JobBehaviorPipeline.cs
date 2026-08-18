namespace Acta.Runtime.Modules.Execution;

/// <summary>
/// Ordered pipeline-behavior resolvers for a worker. <see cref="Build"/> folds them around the
/// innermost handler invocation, once per attempt.
/// </summary>
/// <remarks>
/// Each resolver is a closed-generic <c>sp =&gt; sp.GetRequiredService&lt;TBehavior&gt;()</c> captured at
/// registration, so resolution stays reflection-free with no by-type lookup. An empty set returns the
/// innermost delegate unchanged, so dispatch matches the no-behavior path exactly.
/// </remarks>
internal sealed class JobBehaviorPipeline(IReadOnlyList<Func<IServiceProvider, IJobPipelineBehavior>> resolvers)
{
    private readonly IReadOnlyList<Func<IServiceProvider, IJobPipelineBehavior>> _resolvers = resolvers;

    /// <summary>
    /// Builds the outer-to-inner chain around <paramref name="innermost"/> (the handler invocation
    /// wrapped as a no-arg delegate). The reverse fold makes the last-registered behavior wrap the
    /// handler first, so the first-registered ends up outermost.
    /// </summary>
    public JobBehaviorDelegate Build(
        IServiceProvider attemptServices,
        object request,
        JobContext context,
        JobBehaviorDelegate innermost,
        CancellationToken ct
    )
    {
        if (_resolvers.Count == 0)
        {
            return innermost;
        }

        var next = innermost;
        for (var i = _resolvers.Count - 1; i >= 0; i--)
        {
            var behavior = _resolvers[i](attemptServices);
            var capturedNext = Once(next);
            next = () => behavior.InvokeAsync(request, context, capturedNext, ct);
        }
        return next;
    }

    // A behavior must call next at most once: a second call would re-run the handler within the same
    // attempt. The interface cannot enforce this, so each captured continuation is guarded.
    private static JobBehaviorDelegate Once(JobBehaviorDelegate next)
    {
        var called = 0;
        return () =>
        {
            return Interlocked.Exchange(ref called, 1) != 0
                ? throw new InvalidOperationException("A job pipeline behavior called next more than once.")
                : next();
        };
    }
}
