using Acta.Features.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Acta.Tests.Outbox;

/// <summary>
/// Wiring-level proof that a multi-Run host binds each namespace's outbox relay to ITS OWN source: two
/// namespaces registering two SQLite sources (distinct tables) resolve, through the real composition root,
/// to distinct relay registrations, source store factories, and lazily-built services. Nothing is
/// process-global and no source connection is opened (the factory builds its store without connecting).
/// A full two-namespace live drain is covered at the conformance level; this proves the binding itself.
/// </summary>
public sealed class OutboxRelayWiringTests
{
    private static ServiceProvider BuildTwoNamespaceHost()
    {
        var services = new ServiceCollection();
        services.UseActa(j =>
        {
            j.UseSqlite(o => o.ConnectionString = "Data Source=ledger.db");
            j.DisableCli();
            j.Run(
                "relay-ns-a",
                w =>
                {
                    w.AddModule<TestManifest>();
                    w.AddOutboxRelay(
                        "src-a",
                        s =>
                        {
                            s.Table = "acta_outbox_a";
                            s.QuarantineThreshold = 3;
                            s.UseSqlite(o => o.ConnectionString = "Data Source=source-a.db");
                        }
                    );
                }
            );
            j.Run(
                "relay-ns-b",
                w =>
                {
                    w.AddModule<TestManifest>();
                    w.AddOutboxRelay(
                        "src-b",
                        s =>
                        {
                            s.Table = "acta_outbox_b";
                            s.QuarantineThreshold = 7;
                            s.UseSqlite(o => o.ConnectionString = "Data Source=source-b.db");
                        }
                    );
                }
            );
            // A third namespace with no relay must never appear in the registry.
            j.Run("plain-ns", w => w.AddModule<TestManifest>());
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Each_namespace_binds_to_its_own_source_registration()
    {
        using var sp = BuildTwoNamespaceHost();
        var registry = sp.GetRequiredService<OutboxRelayRegistry>();

        var a = registry.Registration("relay-ns-a");
        var b = registry.Registration("relay-ns-b");

        Assert.Equal("src-a", a.SourceName);
        Assert.Equal("acta_outbox_a", a.Table);
        Assert.Equal(3, a.QuarantineThreshold);
        Assert.Equal("src-b", b.SourceName);
        Assert.Equal("acta_outbox_b", b.Table);
        Assert.Equal(7, b.QuarantineThreshold);

        // No winner-takes-all: each namespace keeps its own provider store factory.
        Assert.NotSame(a.SourceStoreFactory, b.SourceStoreFactory);
    }

    [Fact]
    public void Each_namespace_resolves_a_distinct_cached_relay_service()
    {
        using var sp = BuildTwoNamespaceHost();
        var registry = sp.GetRequiredService<OutboxRelayRegistry>();

        var serviceA = registry.Service("relay-ns-a");
        var serviceB = registry.Service("relay-ns-b");

        // Distinct per-namespace services (each over its own source store), stable across resolutions.
        Assert.NotSame(serviceA, serviceB);
        Assert.Same(serviceA, registry.Service("relay-ns-a"));
    }

    [Fact]
    public void A_namespace_without_a_relay_is_not_registered()
    {
        using var sp = BuildTwoNamespaceHost();
        var registry = sp.GetRequiredService<OutboxRelayRegistry>();

        Assert.Throws<InvalidOperationException>(() => registry.Registration("plain-ns"));
    }

    private sealed class TestManifest : IActaManifest
    {
        public static JobDescriptorManifest Descriptors => new([]);
    }
}
