using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Runtime;

/// <summary>
/// Builder contract for <c>IActaBuilder.Reference</c>: a reference contributes a catalog (namespace +
/// modules) for typed-enqueue route resolution without declaring a worker; repeats dedupe silently.
/// </summary>
public sealed class ReferenceRegistrationTests
{
    [Fact]
    public void Reference_adds_a_catalog_but_no_worker()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.Reference<FakeManifest>("payments");

        Assert.Empty(builder.Workers);
        var catalog = Assert.Single(builder.Catalogs);
        Assert.Equal("payments", catalog.NamespaceName);
        Assert.Equal(typeof(FakeManifest), Assert.Single(catalog.Manifests).ManifestType);
    }

    [Fact]
    public void Run_also_surfaces_as_a_catalog()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.Run<FakeManifest>("payments");

        Assert.Single(builder.Workers);
        var catalog = Assert.Single(builder.Catalogs);
        Assert.Equal("payments", catalog.NamespaceName);
    }

    [Fact]
    public void Reference_same_manifest_and_namespace_twice_is_a_no_op()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.Reference<FakeManifest>("payments").Reference<FakeManifest>("payments");

        Assert.Single(builder.Catalogs);
    }

    [Fact]
    public void Reference_then_Run_same_namespace_yields_both_catalog_entries()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        builder.Reference<FakeManifest>("payments").Run<FakeManifest>("payments");

        Assert.Single(builder.Workers);
        Assert.Equal(2, builder.Catalogs.Count());
    }

    [Fact]
    public void Reference_validates_the_namespace_identifier()
    {
        var builder = new ActaBuilder(new ServiceCollection());

        Assert.ThrowsAny<ArgumentException>(() => builder.Reference<FakeManifest>("Not Kebab!"));
    }

    private sealed class FakeManifest : IJobManifest
    {
        public static JobDescriptorManifest Descriptors { get; } = new([]);
    }
}
