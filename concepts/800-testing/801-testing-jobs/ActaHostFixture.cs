// xUnit class fixture: one ActaTestHost (and its schema migration) per test class, not per [Fact].

using Acta.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Acta.Concepts.Testing;

public sealed class ActaHostFixture : IAsyncLifetime
{
    public IActaTestHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        Host = await ActaTestHost.StartAsync(
            (j, schema) =>
            {
                j.UseLocalDatabase(configuration, schema);
                j.Run<TestingJobs>("testing-jobs");
            }
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.DisposeAsync();
        }
    }
}

// Base for Acta tests: exposes the host, jobs, and the test cancellation token.
public abstract class ActaTestBase(ActaHostFixture acta) : IClassFixture<ActaHostFixture>
{
    protected IActaTestHost Host => acta.Host;

    protected IJobs Jobs => acta.Host.Jobs;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;
}
