using Xunit;

namespace Acta.Tests.Conformance.Testing;

/// <summary>
/// Base class for one-<c>[Fact]</c>-per-class conformance specs. Each subclass declares a single
/// test method; xunit v3 treats every class as its own test collection, so all specs across the
/// assembly run in parallel.
/// </summary>
/// <remarks>
/// Each test instance allocates its schema in <see cref="InitializeAsync"/> and releases it in
/// <see cref="DisposeAsync"/>; with xunit v3's new-instance-per-test default and one-fact-per-class
/// structure, no two tests share state. <typeparamref name="TFixture"/> supplies the per-provider
/// hooks so the spec class stays provider-neutral.
/// </remarks>
/// <typeparam name="TFixture">The per-provider fixture supplying schema lifecycle and catalog queries.</typeparam>
public abstract class IntegrationSpec<TFixture> : IAsyncLifetime
    where TFixture : IConformanceFixture, new()
{
    /// <summary>The fresh fixture allocated for this test instance.</summary>
    protected TFixture Fixture { get; } = new();

    /// <summary>The isolated schema allocated for this test; non-null after <see cref="InitializeAsync"/>.</summary>
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
