using Acta.Runtime.Modules.Execution;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The pipeline fold contract: registration order maps to outer->inner nesting, an empty pipeline is
/// the identity over the handler delegate, short-circuiting skips the handler, exceptions propagate,
/// and a behavior may call <c>next</c> at most once.
/// </summary>
public sealed class JobBehaviorPipelineTests
{
    [Fact]
    public void Build_with_no_behaviors_returns_innermost_unchanged()
    {
        var pipeline = new JobBehaviorPipeline([]);
        JobBehaviorDelegate innermost = () => new ValueTask<JobHandlerInvocationResult>(new JobHandlerInvocationResult(false, null));

        var built = pipeline.Build(NullServiceProvider.Instance, new object(), context: null!, CancellationToken.None, innermost);

        Assert.Same(innermost, built);
    }

    [Fact]
    public async Task Build_invokes_behaviors_outermost_first()
    {
        var log = new List<string>();
        var pipeline = new JobBehaviorPipeline([
            _ => new RecordingBehavior("A", log),
            _ => new RecordingBehavior("B", log),
            _ => new RecordingBehavior("C", log),
        ]);
        ValueTask<JobHandlerInvocationResult> handler()
        {
            log.Add("handler");
            return new ValueTask<JobHandlerInvocationResult>(new JobHandlerInvocationResult(false, null));
        }

        var chain = pipeline.Build(NullServiceProvider.Instance, new object(), context: null!, CancellationToken.None, handler);
        await chain();

        Assert.Equal(["A-pre", "B-pre", "C-pre", "handler", "C-post", "B-post", "A-post"], log);
    }

    [Fact]
    public async Task Build_short_circuit_behavior_skips_handler()
    {
        var handlerRan = false;
        var shortCircuitResult = new JobHandlerInvocationResult(true, "cached");
        var pipeline = new JobBehaviorPipeline([_ => new ShortCircuitBehavior(shortCircuitResult)]);
        ValueTask<JobHandlerInvocationResult> handler()
        {
            handlerRan = true;
            return new ValueTask<JobHandlerInvocationResult>(new JobHandlerInvocationResult(false, null));
        }

        var chain = pipeline.Build(NullServiceProvider.Instance, new object(), context: null!, CancellationToken.None, handler);
        var result = await chain();

        Assert.False(handlerRan);
        Assert.Equal(shortCircuitResult, result);
    }

    [Fact]
    public async Task Build_propagates_handler_exception()
    {
        var thrown = new RescheduleJobException(TimeSpan.FromSeconds(5));
        var pipeline = new JobBehaviorPipeline([_ => new RecordingBehavior("A", [])]);
        ValueTask<JobHandlerInvocationResult> handler() => throw thrown;

        var chain = pipeline.Build(NullServiceProvider.Instance, new object(), context: null!, CancellationToken.None, handler);

        var caught = await Assert.ThrowsAsync<RescheduleJobException>(async () => await chain());
        Assert.Same(thrown, caught);
    }

    [Fact]
    public async Task Build_throws_when_behavior_calls_next_twice()
    {
        var handlerRuns = 0;
        var pipeline = new JobBehaviorPipeline([_ => new DoubleNextBehavior()]);
        ValueTask<JobHandlerInvocationResult> handler()
        {
            handlerRuns++;
            return new ValueTask<JobHandlerInvocationResult>(new JobHandlerInvocationResult(false, null));
        }

        var chain = pipeline.Build(NullServiceProvider.Instance, new object(), context: null!, CancellationToken.None, handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await chain());
        Assert.Equal(1, handlerRuns);
    }

    private sealed class RecordingBehavior(string tag, List<string> log) : IJobPipelineBehavior
    {
        public async ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        )
        {
            log.Add($"{tag}-pre");
            var result = await next();
            log.Add($"{tag}-post");
            return result;
        }
    }

    private sealed class ShortCircuitBehavior(JobHandlerInvocationResult result) : IJobPipelineBehavior
    {
        public ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        ) => new(result);
    }

    private sealed class DoubleNextBehavior : IJobPipelineBehavior
    {
        public async ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        )
        {
            await next();
            return await next();
        }
    }

    private sealed class NullServiceProvider : IServiceProvider
    {
        public static readonly NullServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}
