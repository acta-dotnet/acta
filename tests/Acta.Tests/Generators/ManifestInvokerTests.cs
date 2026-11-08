using Microsoft.Extensions.DependencyInjection;
using TestJobs;
using Xunit;

namespace Acta.Tests.Generators;

/// <summary>
/// Runs the generated invokers of <see cref="TestJobsManifest"/> against a real service provider: how an instance
/// handler is activated and who disposes it.
/// </summary>
public sealed class ManifestInvokerTests
{
    [Fact]
    public async Task A_registered_handler_is_the_instance_invoked()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<TypedClientProbe>(http => http.BaseAddress = new Uri("https://hooks.example/"));

        var output = await InvokeAsync(services, "typed-client-probe");

        Assert.Equal("https://hooks.example/", output.Result);
    }

    // A handler the invoker constructs is disposed on every exit, through DisposeAsync when it has one.
    [Fact]
    public async Task A_constructed_handler_is_disposed_when_the_attempt_ends()
    {
        var log = new DisposalLog();
        var services = new ServiceCollection().AddSingleton(log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(services, "disposable-probe"));
        await InvokeAsync(services, "async-disposable-probe");

        Assert.Equal(["Run", "Dispose", "Run", "DisposeAsync"], log.Calls);
    }

    // A handler DI cannot build fails as HandlerActivationException, which the worker never retries, whether the
    // invoker constructs it or a registration of the handler itself cannot be resolved.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_handler_whose_dependency_is_not_registered_throws_HandlerActivationException(bool handlerRegistered)
    {
        var services = new ServiceCollection();
        if (handlerRegistered)
        {
            services.AddScoped<MissingServiceProbe>();
        }

        var ex = await Assert.ThrowsAsync<HandlerActivationException>(() => InvokeAsync(services, "missing-service-probe"));

        var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal($"Handler 'TestJobs.MissingServiceProbe' cannot be activated: {inner.Message}", ex.Message);
        Assert.Contains("'TestJobs.UnregisteredDependency'", inner.Message, StringComparison.Ordinal);
    }

    // A scope that shutdown already tore down is no fault of the handler's registrations, so the attempt keeps its retry.
    [Fact]
    public async Task A_disposed_scope_is_not_an_activation_failure()
    {
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var scope = provider.CreateAsyncScope();
        await scope.DisposeAsync();
        var descriptor = TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == "missing-service-probe");

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await descriptor.Invoker(scope.ServiceProvider, new NoInput(), null!, TestContext.Current.CancellationToken)
        );
    }

    private static async Task<JobHandlerOutput> InvokeAsync(IServiceCollection services, string jobName)
    {
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var descriptor = TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == jobName);
        return await descriptor.Invoker(scope.ServiceProvider, new NoInput(), null!, TestContext.Current.CancellationToken);
    }
}
