// xUnit class fixture: one ActaTestHost per test class, like 801, for a durable multi-step job.

using Acta.Testing.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Acta.Concepts.TestingDurable;

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
                j.Run<TestingDurableJobs>("testing-durable");
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

public abstract class ActaTestBase(ActaHostFixture acta) : IClassFixture<ActaHostFixture>
{
    protected IActaTestHost Host => acta.Host;

    protected IJobs Jobs => acta.Host.Jobs;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;
}
