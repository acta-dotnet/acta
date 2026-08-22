using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Base for relational provider specs. Uses the internal test-query session for setup/assertions and
/// seeds the per-test namespace row via <see cref="ActaTestSeeder"/>.
/// </summary>
public abstract class ActaStorageTestBase<TFixture> : ActaTestBase<TFixture>
    where TFixture : IConformanceFixture, new()
{
    private protected ActaTestSeeder Seeder { get; private set; } = null!;

    protected int TestNamespaceId { get; private set; }

    protected override void ConfigureServices(IServiceCollection services, string testNamespace)
    {
        services.UseActa(j => Fixture.ApplyProvider(j, Schema.SchemaName));
    }

    protected override async ValueTask AfterInitializeAsync()
    {
        Seeder = new ActaTestSeeder(Db);
        TestNamespaceId = await Seeder.SeedJobNamespaceAsync(TestNamespace, ownerTeam: "test", ct: TestContext.Current.CancellationToken);
    }
}
