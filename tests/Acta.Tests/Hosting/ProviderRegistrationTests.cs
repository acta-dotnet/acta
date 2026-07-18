using Acta.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Hosting;

public sealed class ProviderRegistrationTests
{
    [Fact]
    public void Different_providers_fail_before_adding_a_mixed_graph()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.UseActa(builder =>
            {
                builder.UsePostgres(static _ => { });
                builder.UseSqlite(static _ => { });
            })
        );

        Assert.Contains("'Postgres'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'Sqlite'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one durable provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DbProvider.Postgres, Assert.Single(ProviderInfos(services)).Provider);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProviderBootstrap));
    }

    [Fact]
    public void Registering_the_same_provider_twice_fails_before_adding_duplicate_services()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.UseActa(builder =>
            {
                builder.UseSqlite(static _ => { });
                builder.UseSqlite(static _ => { });
            })
        );

        Assert.Contains("'Sqlite' is already registered", exception.Message, StringComparison.Ordinal);
        Assert.Contains("register 'Sqlite' again", exception.Message, StringComparison.Ordinal);
        Assert.Equal(DbProvider.Sqlite, Assert.Single(ProviderInfos(services)).Provider);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IProviderBootstrap));
    }

    [Fact]
    public void UseActa_rejects_multiple_provider_markers_that_bypass_provider_extensions()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.UseActa(builder =>
            {
                builder.Services.AddSingleton(new ActaProviderInfo(DbProvider.Postgres, SupportsRoutines: true));
                builder.Services.AddSingleton(new ActaProviderInfo(DbProvider.Sqlite, SupportsRoutines: false));
            })
        );

        Assert.Contains("requires exactly one durable provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Postgres, Sqlite", exception.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<ActaProviderInfo> ProviderInfos(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(ActaProviderInfo))
            .Select(descriptor => Assert.IsType<ActaProviderInfo>(descriptor.ImplementationInstance));
}
