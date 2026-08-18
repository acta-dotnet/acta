using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Base class for one-<c>[Fact]</c>-per-class conformance specs. Each subclass declares a single
/// test method; xunit v3 treats every class as its own test collection, so all specs across the
/// assembly run in parallel.
/// </summary>
/// <remarks>
/// The schema is shared, not per-test. <see cref="InitializeAsync"/> calls
/// <see cref="IConformanceFixture.CreateSchemaAsync"/>, which hands back a handle to the one process-wide
/// <c>acta_test</c> schema (bootstrapped once behind the provider's cache and never torn down), and
/// <see cref="DisposeAsync"/> is a no-op on it so rows persist for inspection. xunit v3's
/// new-instance-per-test default gives each test a fresh spec object and fixture, but every one of them
/// reads and writes the same tables, so a spec isolates itself by owning its rows - a unique namespace, a
/// unique deduplication key, a filter on ids it created - and never by assuming an empty schema. Reset is
/// an explicit operator action via <c>DatabaseSetup.ResetActaTestSchema</c>.
/// <typeparamref name="TFixture"/> supplies the per-provider hooks so the spec class stays
/// provider-neutral.
/// </remarks>
/// <typeparam name="TFixture">The per-provider fixture supplying schema lifecycle and catalog queries.</typeparam>
public abstract class IntegrationSpec<TFixture> : IAsyncLifetime
    where TFixture : IConformanceFixture, new()
{
    /// <summary>The fresh fixture allocated for this test instance.</summary>
    protected TFixture Fixture { get; } = new();

    /// <summary>The shared <c>acta_test</c> schema handle; non-null after <see cref="InitializeAsync"/>.</summary>
    protected IIntegrationSchema Schema { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Schema = await Fixture.CreateSchemaAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Schema is not null)
        {
            await Schema.DisposeAsync();
        }
    }
}
