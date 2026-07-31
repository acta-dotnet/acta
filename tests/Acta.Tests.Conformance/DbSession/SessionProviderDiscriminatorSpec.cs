using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.DbSession;

/// <summary>
/// Conformance: provider registration wires <c>IDbSession</c> with the correct provider discriminator
/// and configured schema.
/// </summary>
[ConformanceSpec(
    "provider.discriminator",
    "Provider registration surfaces the discriminator and schema",
    Area = "Provider",
    Contract = "Provider registration wires IDbSession with the correct provider discriminator and the configured schema.",
    Arrange = "A service collection registers the provider under the test schema.",
    Act = "IDbSession is resolved and its testing raw-connection helper is opened.",
    Assert = "The session surfaces the correct provider discriminator and configured schema and the raw connection opens."
)]
public abstract class SessionProviderDiscriminatorSpec<TFixture> : IntegrationSpec<TFixture>
    where TFixture : IConformanceFixture, new()
{
    protected abstract DbProvider ExpectedProvider { get; }

    [Fact(DisplayName = "Resolved IDbSession surfaces provider metadata and opens a raw test connection")]
    public async Task ActaDb_SurfacesProviderAndSchemaAndOpensTestConnection()
    {
        var services = new ServiceCollection();
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var db = provider.GetRequiredService<IDbSession>();

        Assert.Equal(ExpectedProvider, db.Provider);
        Assert.Equal(Schema.SchemaName, db.Schema);

        await using var connection = await db.GetConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact(DisplayName = "Provider IDbSession is singleton and scope-independent")]
    public void ActaDb_IsSingletonAndScopeIndependent()
    {
        var services = new ServiceCollection();
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));

        var descriptor = Assert.Single(services.Where(d => d.ServiceType == typeof(IDbSession)));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var root = provider.GetRequiredService<IDbSession>();
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<IDbSession>();

        Assert.Same(root, scoped);
    }
}
