using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// The builder contract for <c>AddPipelineBehavior</c>: behaviors register scoped, keep registration
/// order, and a repeated registration is a no-op. Order is what the runtime folds into outer->inner
/// nesting, so it is part of the contract.
/// </summary>
public sealed class AddPipelineBehaviorRegistrationTests
{
    [Fact]
    public void AddPipelineBehavior_registers_the_type_scoped()
    {
        var services = new ServiceCollection();

        new JobsBuilder(services).AddPipelineBehavior<BehaviorA>();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(BehaviorA));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void AddPipelineBehavior_registers_singleton_when_overridden()
    {
        var services = new ServiceCollection();

        new JobsBuilder(services).AddPipelineBehavior<BehaviorA>(ServiceLifetime.Singleton);

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(BehaviorA));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void AddPipelineBehavior_preserves_registration_order()
    {
        var services = new ServiceCollection();
        var builder = new JobsBuilder(services);

        builder.AddPipelineBehavior<BehaviorA>().AddPipelineBehavior<BehaviorB>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = builder.PipelineBehaviors.Select(r => r(scope.ServiceProvider).GetType()).ToArray();

        Assert.Equal([typeof(BehaviorA), typeof(BehaviorB)], resolved);
    }

    [Fact]
    public void AddPipelineBehavior_is_a_no_op_for_a_duplicate_type()
    {
        var services = new ServiceCollection();
        var builder = new JobsBuilder(services);

        builder.AddPipelineBehavior<BehaviorA>().AddPipelineBehavior<BehaviorA>();

        Assert.Single(builder.PipelineBehaviors);
        Assert.Single(services, d => d.ServiceType == typeof(BehaviorA));
    }

    private sealed class BehaviorA : IJobPipelineBehavior
    {
        public ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        ) => next();
    }

    private sealed class BehaviorB : IJobPipelineBehavior
    {
        public ValueTask<JobHandlerInvocationResult> InvokeAsync(
            object request,
            JobContext context,
            JobBehaviorDelegate next,
            CancellationToken ct
        ) => next();
    }
}
