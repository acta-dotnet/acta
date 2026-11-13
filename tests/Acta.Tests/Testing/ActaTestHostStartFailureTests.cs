using System.Runtime.CompilerServices;
using Acta.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Testing;

/// <summary>
/// A start that fails hands the caller no host to dispose, so the start disposes the services it built before the
/// failure reaches the caller: no connection, timer, or in-memory database outlives it.
/// </summary>
public sealed class ActaTestHostStartFailureTests
{
    [Fact]
    public async Task A_start_that_fails_disposes_the_services_it_built_and_throws_the_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var disposed = new StrongBox<bool>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActaTestHost.StartAsync(
                (j, _) => j.UseSqliteEphemeral(),
                // The provider builds the bootstrap from its factory, so the provider owns it and its disposal. It is
                // registered first, so the start fails before the database's own bootstrap migrates anything.
                new ActaTestHostOptions
                {
                    ConfigureServices = s =>
                        s.Insert(0, ServiceDescriptor.Singleton<IProviderBootstrap>(_ => new FailingBootstrap(disposed))),
                },
                ct
            )
        );

        Assert.Equal(FailingBootstrap.Failure, failure.Message);
        Assert.True(disposed.Value);
    }

    /// <summary>Fails the start, and disposes only asynchronously: the provider's synchronous dispose throws on such a service.</summary>
    private sealed class FailingBootstrap(StrongBox<bool> disposed) : IProviderBootstrap, IAsyncDisposable
    {
        public const string Failure = "The bootstrap failed.";

        public Task RunAsync(CancellationToken ct) => throw new InvalidOperationException(Failure);

        public ValueTask DisposeAsync()
        {
            disposed.Value = true;
            return ValueTask.CompletedTask;
        }
    }
}
