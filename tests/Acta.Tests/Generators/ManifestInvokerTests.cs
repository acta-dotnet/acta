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

    private static async Task<JobHandlerOutput> InvokeAsync(IServiceCollection services, string jobName)
    {
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var descriptor = TestJobsManifest.Descriptors.Descriptors.Single(d => d.JobName == jobName);
        return await descriptor.Invoker(scope.ServiceProvider, new NoInput(), null!, TestContext.Current.CancellationToken);
    }
}
