using Acta.Tests.Conformance.Contracts;
using Acta.Tests.Conformance.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Db;

/// <summary>
/// Conformance: provider registration wires <c>IDbSession</c> with the correct provider discriminator
/// and configured schema.
/// </summary>
[ConformanceSpec(
    "provider.discriminator",
    "Provider registration surfaces the discriminator and schema",
    Area = "Provider",
    Contract = "Provider registration wires IDbSession with the correct provider discriminator and the configured schema."
)]
public sealed class SessionProviderDiscriminatorSpec : IntegrationSpec<ConformanceFixture>
{
    private DbProvider ExpectedProvider =>
        Fixture.DialectToken switch
        {
            "pg" => DbProvider.Postgres,
            "mssql" => DbProvider.SqlServer,
            "sqlite" => DbProvider.Sqlite,
            var token => throw new InvalidOperationException($"Unknown dialect token '{token}'."),
        };

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

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IDbSession));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var root = provider.GetRequiredService<IDbSession>();
        using var scope = provider.CreateScope();
        var scoped = scope.ServiceProvider.GetRequiredService<IDbSession>();

        Assert.Same(root, scoped);
    }
}
